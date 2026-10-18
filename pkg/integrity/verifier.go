package integrity

import (
	"crypto/ed25519"
	"crypto/subtle"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
)

// FileMismatch records a difference between the manifest entry and the file on disk.
type FileMismatch struct {
	Path           string `json:"path"`
	ExpectedSHA256 string `json:"expected_sha256"`
	ActualSHA256   string `json:"actual_sha256"`
	ExpectedSize   int64  `json:"expected_size"`
	ActualSize     int64  `json:"actual_size"`
	Reason         string `json:"reason"`
}

// VerificationResult summarizes the installed-file integrity status.
type VerificationResult struct {
	Valid         bool           `json:"valid"`
	TotalFiles    int            `json:"total_files"`
	PassedFiles   int            `json:"passed_files"`
	MissingFiles  []string       `json:"missing_files,omitempty"`
	ModifiedFiles []FileMismatch `json:"modified_files,omitempty"`
	Errors        []string       `json:"errors,omitempty"`
}

// DecodeBase64Flex decodes unpadded base64url or standard base64 strings.
func DecodeBase64Flex(s string) ([]byte, error) {
	b, err := base64.RawURLEncoding.DecodeString(s)
	if err == nil {
		return b, nil
	}
	b, err = base64.URLEncoding.DecodeString(s)
	if err == nil {
		return b, nil
	}
	b, err = base64.StdEncoding.DecodeString(s)
	if err == nil {
		return b, nil
	}
	return base64.RawStdEncoding.DecodeString(s)
}

// VerifyEnvelope verifies the digital signature of a signed integrity envelope against
// the set of trusted public keys, then parses and validates the manifest payload.
func VerifyEnvelope(env *SignedEnvelope, trustedKeys map[string]ed25519.PublicKey) (*Manifest, error) {
	if env == nil {
		return nil, errors.New("signed envelope cannot be nil")
	}
	if env.Format != FormatIdentifier {
		return nil, fmt.Errorf("%w: got %q, want %q", ErrUnknownFormat, env.Format, FormatIdentifier)
	}
	if env.ManifestVersion != CurrentManifestVersion {
		return nil, fmt.Errorf("%w: version %d is not supported", ErrUnsupportedVersion, env.ManifestVersion)
	}
	if env.Algorithm != AlgorithmEd25519 {
		return nil, fmt.Errorf("%w: got %q", ErrUnsupportedAlgorithm, env.Algorithm)
	}
	if env.SigningKeyID == "" {
		return nil, errors.New("signing_key_id is missing from envelope")
	}

	pubKey, ok := trustedKeys[env.SigningKeyID]
	if !ok || len(pubKey) != ed25519.PublicKeySize {
		return nil, fmt.Errorf("%w: key ID %q", ErrUnknownSigningKey, env.SigningKeyID)
	}

	sigBytes, err := DecodeBase64Flex(env.Signature)
	if err != nil {
		return nil, fmt.Errorf("decode signature: %w", err)
	}
	if len(sigBytes) != ed25519.SignatureSize {
		return nil, fmt.Errorf("invalid signature size: expected %d bytes, got %d",
			ed25519.SignatureSize, len(sigBytes))
	}

	// Verify Ed25519 signature over the payload string bytes
	if !ed25519.Verify(pubKey, []byte(env.Payload), sigBytes) {
		return nil, ErrInvalidSignature
	}

	// Decode the canonical JSON payload
	payloadBytes, err := DecodeBase64Flex(env.Payload)
	if err != nil {
		return nil, fmt.Errorf("decode payload: %w", err)
	}

	var m Manifest
	if err := json.Unmarshal(payloadBytes, &m); err != nil {
		return nil, fmt.Errorf("unmarshal verified payload: %w", err)
	}

	if m.ManifestVersion != CurrentManifestVersion {
		return nil, fmt.Errorf("%w: payload version %d", ErrUnsupportedVersion, m.ManifestVersion)
	}
	if m.SigningKeyID != env.SigningKeyID {
		return nil, fmt.Errorf("signing_key_id mismatch between envelope (%q) and payload (%q)",
			env.SigningKeyID, m.SigningKeyID)
	}

	// Validate path safety on all entries to ensure no path traversal attacks
	for _, f := range m.Files {
		if err := ValidateRelativePath(f.Path); err != nil {
			return nil, fmt.Errorf("security violation in manifest: %w", err)
		}
	}

	return &m, nil
}

// VerifyInstalledFiles verifies every file listed in the manifest against the actual files
// present in installDir. It enforces strict path traversal protection, size checks, and
// constant-time cryptographic hash comparison.
func VerifyInstalledFiles(m *Manifest, installDir string) (*VerificationResult, error) {
	if m == nil {
		return nil, errors.New("manifest cannot be nil")
	}
	if installDir == "" {
		return nil, errors.New("installDir cannot be empty")
	}

	cleanRoot, err := filepath.Abs(installDir)
	if err != nil {
		return nil, fmt.Errorf("resolve installDir: %w", err)
	}
	rootPrefix := cleanRoot + string(filepath.Separator)

	res := &VerificationResult{
		TotalFiles: len(m.Files),
	}

	for _, entry := range m.Files {
		if err := ValidateRelativePath(entry.Path); err != nil {
			res.Errors = append(res.Errors, fmt.Sprintf("invalid path %q: %v", entry.Path, err))
			continue
		}

		nativeRel := filepath.FromSlash(entry.Path)
		targetPath := filepath.Join(cleanRoot, nativeRel)

		// Enforce strict directory traversal jail
		absTarget, err := filepath.Abs(targetPath)
		if err != nil || (!strings.HasPrefix(absTarget, rootPrefix) && absTarget != cleanRoot) {
			res.Errors = append(res.Errors, fmt.Sprintf("path traversal blocked for %q -> %q", entry.Path, absTarget))
			continue
		}

		info, err := os.Lstat(absTarget)
		if err != nil {
			if os.IsNotExist(err) {
				res.MissingFiles = append(res.MissingFiles, entry.Path)
			} else {
				res.Errors = append(res.Errors, fmt.Sprintf("stat %q: %v", entry.Path, err))
			}
			continue
		}

		// Reject symbolic links pointing outside the installation root
		if info.Mode()&os.ModeSymlink != 0 {
			evalPath, err := filepath.EvalSymlinks(absTarget)
			if err != nil || (!strings.HasPrefix(evalPath, rootPrefix) && evalPath != cleanRoot) {
				res.ModifiedFiles = append(res.ModifiedFiles, FileMismatch{
					Path:   entry.Path,
					Reason: "symlink points outside protected installation directory",
				})
				continue
			}
		}

		// Size check fast-path
		if info.Size() != entry.Size {
			res.ModifiedFiles = append(res.ModifiedFiles, FileMismatch{
				Path:           entry.Path,
				ExpectedSize:   entry.Size,
				ActualSize:     info.Size(),
				ExpectedSHA256: entry.SHA256,
				Reason:         fmt.Sprintf("file size mismatch (expected %d, got %d)", entry.Size, info.Size()),
			})
			continue
		}

		// Streaming SHA-256 computation
		actualHash, _, err := HashFile(absTarget)
		if err != nil {
			res.Errors = append(res.Errors, fmt.Sprintf("hash file %q: %v", entry.Path, err))
			continue
		}

		// Constant-time digest comparison to avoid timing leaks
		if subtle.ConstantTimeCompare([]byte(strings.ToLower(actualHash)), []byte(strings.ToLower(entry.SHA256))) != 1 {
			res.ModifiedFiles = append(res.ModifiedFiles, FileMismatch{
				Path:           entry.Path,
				ExpectedSHA256: strings.ToLower(entry.SHA256),
				ActualSHA256:   strings.ToLower(actualHash),
				ExpectedSize:   entry.Size,
				ActualSize:     info.Size(),
				Reason:         "sha256 digest mismatch (content modified)",
			})
			continue
		}

		res.PassedFiles++
	}

	res.Valid = len(res.MissingFiles) == 0 && len(res.ModifiedFiles) == 0 && len(res.Errors) == 0
	return res, nil
}
