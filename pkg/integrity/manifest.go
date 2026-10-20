package integrity

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"path"
	"sort"
	"strings"
	"time"
)

const (
	CurrentManifestVersion = 1
	FormatIdentifier       = "keygate-integrity-v1"
	AlgorithmEd25519       = "ed25519"
)

// Sentinel errors for integrity manifest verification
var (
	ErrUnknownFormat       = errors.New("unsupported manifest format identifier")
	ErrUnsupportedVersion  = errors.New("unsupported manifest version")
	ErrUnsupportedAlgorithm= errors.New("unsupported signature algorithm (only ed25519 is supported)")
	ErrUnknownSigningKey   = errors.New("unknown signing key ID; key not found in trusted set")
	ErrInvalidSignature    = errors.New("cryptographic signature verification failed; manifest is untrusted")
	ErrPathTraversal       = errors.New("path traversal detected: relative path attempts to escape root directory")
	ErrInvalidPath         = errors.New("invalid file path: contains forbidden characters, backslashes, or control bytes")
)

// FileEntry represents a single protected file in the manifest.
type FileEntry struct {
	Path   string `json:"path"`   // Relative POSIX path using forward slash
	SHA256 string `json:"sha256"` // 64-character lowercase hexadecimal SHA-256 digest
	Size   int64  `json:"size"`   // File size in bytes (>= 0)
}

// Manifest represents the unencoded, canonical application integrity manifest.
type Manifest struct {
	ManifestVersion int               `json:"manifest_version"`
	ProductID       string            `json:"product_id"`
	ReleaseID       string            `json:"release_id,omitempty"`
	Version         string            `json:"version"`
	Channel         string            `json:"channel,omitempty"`
	Platform        string            `json:"platform,omitempty"`
	Arch            string            `json:"arch,omitempty"`
	CreatedAt       string            `json:"created_at"` // RFC 3339 UTC
	SigningKeyID    string            `json:"signing_key_id"`
	Metadata        map[string]string `json:"metadata,omitempty"`
	Files           []FileEntry       `json:"files"`
}

// SignedEnvelope is the self-contained container holding the signed payload and signature.
type SignedEnvelope struct {
	Format          string    `json:"format"`           // "keygate-integrity-v1"
	ManifestVersion int       `json:"manifest_version"` // 1
	Payload         string    `json:"payload"`          // base64url(canonical_manifest_json)
	Signature       string    `json:"signature"`        // base64url(ed25519_signature)
	SigningKeyID    string    `json:"signing_key_id"`   // key ID matching keygate release key
	Algorithm       string    `json:"algorithm"`        // "ed25519"
	Manifest        *Manifest `json:"manifest,omitempty"` // optional unencoded payload for human inspection
}

// ValidateRelativePath validates that a relative file path is strictly safe:
// - POSIX forward slashes only (no backslashes)
// - No leading slash, drive letters, or empty components
// - No directory traversal segments ('.' or '..')
// - No control characters or null bytes
func ValidateRelativePath(relPath string) error {
	if relPath == "" {
		return fmt.Errorf("%w: path cannot be empty", ErrInvalidPath)
	}
	if strings.Contains(relPath, "\\") {
		return fmt.Errorf("%w: path must use forward slashes '/', found backslash", ErrInvalidPath)
	}
	if strings.HasPrefix(relPath, "/") {
		return fmt.Errorf("%w: path cannot be absolute", ErrPathTraversal)
	}
	if strings.Contains(relPath, "\x00") {
		return fmt.Errorf("%w: path contains null byte", ErrInvalidPath)
	}

	for _, r := range relPath {
		if r < 32 || r == 127 {
			return fmt.Errorf("%w: path contains control character (code %d)", ErrInvalidPath, r)
		}
	}

	// Check components
	parts := strings.Split(relPath, "/")
	for _, p := range parts {
		if p == "" {
			return fmt.Errorf("%w: path contains empty component or consecutive slashes", ErrInvalidPath)
		}
		if p == "." || p == ".." {
			return fmt.Errorf("%w: segment %q violates directory traversal boundary", ErrPathTraversal, p)
		}
	}

	cleaned := path.Clean(relPath)
	if cleaned != relPath {
		return fmt.Errorf("%w: path is not in canonical form (%q vs %q)", ErrInvalidPath, relPath, cleaned)
	}
	if strings.HasPrefix(cleaned, "../") || cleaned == ".." {
		return fmt.Errorf("%w: cleaned path escapes root", ErrPathTraversal)
	}

	return nil
}

// Canonicalize produces deterministic JSON serialization of the manifest:
// - Sorts all files by Path lexicographically
// - Formats without insignificant whitespace
// - Encodes strictly in UTF-8
func Canonicalize(m *Manifest) ([]byte, error) {
	if m == nil {
		return nil, errors.New("manifest cannot be nil")
	}

	// Ensure files are sorted deterministically
	sort.Slice(m.Files, func(i, j int) bool {
		return m.Files[i].Path < m.Files[j].Path
	})

	// Validate every path
	for _, f := range m.Files {
		if err := ValidateRelativePath(f.Path); err != nil {
			return nil, fmt.Errorf("invalid path in manifest %q: %w", f.Path, err)
		}
		if len(f.SHA256) != 64 {
			return nil, fmt.Errorf("invalid sha256 digest length for %q: %d (expected 64)", f.Path, len(f.SHA256))
		}
		if f.Size < 0 {
			return nil, fmt.Errorf("invalid negative file size for %q: %d", f.Path, f.Size)
		}
	}

	// Default created_at to now in UTC RFC3339 if empty
	if m.CreatedAt == "" {
		m.CreatedAt = time.Now().UTC().Format(time.RFC3339)
	}
	if m.ManifestVersion == 0 {
		m.ManifestVersion = CurrentManifestVersion
	}

	// Compact JSON marshaling (no indentation)
	raw, err := json.Marshal(m)
	if err != nil {
		return nil, fmt.Errorf("canonical json marshal: %w", err)
	}

	// Format through standard compact buffer to guarantee deterministic separators
	var compactBuf bytes.Buffer
	if err := json.Compact(&compactBuf, raw); err != nil {
		return nil, fmt.Errorf("json compact: %w", err)
	}

	return compactBuf.Bytes(), nil
}

// HashFile streams a file from disk and computes its SHA-256 digest and size.
func HashFile(filePath string) (string, int64, error) {
	f, err := os.Open(filePath)
	if err != nil {
		return "", 0, err
	}
	defer f.Close()

	h := sha256.New()
	size, err := io.Copy(h, f)
	if err != nil {
		return "", 0, fmt.Errorf("read file: %w", err)
	}

	digest := hex.EncodeToString(h.Sum(nil))
	return digest, size, nil
}
