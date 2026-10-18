package integrity_test

import (
	"crypto/ed25519"
	"crypto/rand"
	"encoding/base64"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/tabloy/keygate/pkg/integrity"
)

// Helper to create synthetic test keypairs
func generateTestKeypair(t *testing.T) (ed25519.PublicKey, ed25519.PrivateKey) {
	t.Helper()
	pub, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("generate test key: %v", err)
	}
	return pub, priv
}

// 1. Valid Manifests & Signature Verification
func TestIntegrity_ValidManifestAndSignature(t *testing.T) {
	pub, priv := generateTestKeypair(t)
	keyID := "test_key_2026_01"

	tempDir := t.TempDir()
	file1Path := filepath.Join(tempDir, "bin", "app")
	file2Path := filepath.Join(tempDir, "config.json")

	if err := os.MkdirAll(filepath.Dir(file1Path), 0755); err != nil {
		t.Fatalf("mkdir: %v", err)
	}
	if err := os.WriteFile(file1Path, []byte("BINARY_CONTENT_V1"), 0755); err != nil {
		t.Fatalf("write file1: %v", err)
	}
	if err := os.WriteFile(file2Path, []byte(`{"env":"production"}`), 0644); err != nil {
		t.Fatalf("write file2: %v", err)
	}

	entries, err := integrity.ScanReleaseDirectory(integrity.ScanOptions{
		BaseDir: tempDir,
	})
	if err != nil {
		t.Fatalf("scan directory: %v", err)
	}
	if len(entries) != 2 {
		t.Fatalf("expected 2 files, found %d", len(entries))
	}

	manifest := &integrity.Manifest{
		ProductID: "prd_sample",
		ReleaseID: "rel_12345",
		Version:   "1.0.0",
		Platform:  "darwin",
		Arch:      "arm64",
		CreatedAt: "2026-10-18T00:00:00Z",
		Files:     entries,
	}

	env, err := integrity.SignManifest(manifest, priv, keyID)
	if err != nil {
		t.Fatalf("sign manifest: %v", err)
	}

	trustedKeys := map[string]ed25519.PublicKey{
		keyID: pub,
	}

	verified, err := integrity.VerifyEnvelope(env, trustedKeys)
	if err != nil {
		t.Fatalf("verify envelope: %v", err)
	}
	if verified.Version != "1.0.0" {
		t.Errorf("version = %q, want 1.0.0", verified.Version)
	}

	// Verify installed files on disk
	res, err := integrity.VerifyInstalledFiles(verified, tempDir)
	if err != nil {
		t.Fatalf("verify installed files: %v", err)
	}
	if !res.Valid {
		t.Errorf("expected valid verification, got invalid: %+v", res)
	}
	if res.PassedFiles != 2 {
		t.Errorf("passed files = %d, want 2", res.PassedFiles)
	}
}

// 2. Modified Files (Tampering detection)
func TestIntegrity_ModifiedFileDetection(t *testing.T) {
	pub, priv := generateTestKeypair(t)
	keyID := "test_key_mod"

	tempDir := t.TempDir()
	appFile := filepath.Join(tempDir, "app.bin")
	if err := os.WriteFile(appFile, []byte("ORIGINAL_CONTENT"), 0755); err != nil {
		t.Fatalf("write app: %v", err)
	}

	entries, err := integrity.ScanReleaseDirectory(integrity.ScanOptions{BaseDir: tempDir})
	if err != nil {
		t.Fatalf("scan: %v", err)
	}

	manifest := &integrity.Manifest{
		ProductID: "prd_mod",
		Version:   "1.0.0",
		CreatedAt: "2026-10-18T00:00:00Z",
		Files:     entries,
	}
	env, err := integrity.SignManifest(manifest, priv, keyID)
	if err != nil {
		t.Fatalf("sign: %v", err)
	}

	// Attacker modifies the file on disk
	if err := os.WriteFile(appFile, []byte("TAMPERED_INJECTED_CONTENT"), 0755); err != nil {
		t.Fatalf("tamper file: %v", err)
	}

	trustedKeys := map[string]ed25519.PublicKey{keyID: pub}
	verified, err := integrity.VerifyEnvelope(env, trustedKeys)
	if err != nil {
		t.Fatalf("verify envelope: %v", err)
	}

	res, err := integrity.VerifyInstalledFiles(verified, tempDir)
	if err != nil {
		t.Fatalf("verify installed files: %v", err)
	}
	if res.Valid {
		t.Error("expected verification failure for tampered file, but got valid")
	}
	if len(res.ModifiedFiles) != 1 {
		t.Fatalf("expected 1 modified file, got %d", len(res.ModifiedFiles))
	}
	if res.ModifiedFiles[0].Path != "app.bin" {
		t.Errorf("modified path = %q, want app.bin", res.ModifiedFiles[0].Path)
	}
}

// 3. Missing Files
func TestIntegrity_MissingFileDetection(t *testing.T) {
	pub, priv := generateTestKeypair(t)
	keyID := "test_key_missing"

	tempDir := t.TempDir()
	f1 := filepath.Join(tempDir, "f1.txt")
	f2 := filepath.Join(tempDir, "f2.txt")
	_ = os.WriteFile(f1, []byte("F1"), 0644)
	_ = os.WriteFile(f2, []byte("F2"), 0644)

	entries, _ := integrity.ScanReleaseDirectory(integrity.ScanOptions{BaseDir: tempDir})
	manifest := &integrity.Manifest{
		ProductID: "prd_missing",
		Version:   "1.0.0",
		CreatedAt: "2026-10-18T00:00:00Z",
		Files:     entries,
	}
	env, _ := integrity.SignManifest(manifest, priv, keyID)

	// Delete f2
	_ = os.Remove(f2)

	trustedKeys := map[string]ed25519.PublicKey{keyID: pub}
	verified, _ := integrity.VerifyEnvelope(env, trustedKeys)

	res, err := integrity.VerifyInstalledFiles(verified, tempDir)
	if err != nil {
		t.Fatalf("verify installed files: %v", err)
	}
	if res.Valid {
		t.Error("expected failure due to missing file, got valid")
	}
	if len(res.MissingFiles) != 1 || res.MissingFiles[0] != "f2.txt" {
		t.Errorf("missing files = %v, want ['f2.txt']", res.MissingFiles)
	}
}

// 4. Path Traversal Rejection
func TestIntegrity_PathTraversalPrevention(t *testing.T) {
	traversals := []string{
		"../escape.txt",
		"../../etc/passwd",
		"/absolute/path/file",
		"foo/../../bar",
		"folder\\backslashes\\file",
		"folder//double_slash",
		"folder/./dot_segment",
		"folder/null\x00byte",
	}

	for _, badPath := range traversals {
		t.Run(badPath, func(t *testing.T) {
			err := integrity.ValidateRelativePath(badPath)
			if err == nil {
				t.Errorf("expected error for path traversal %q, got nil", badPath)
			}
		})
	}
}

// 5. Signature Rejection: Untrusted Key & Tampered Payload
func TestIntegrity_SignatureRejections(t *testing.T) {
	pub1, priv1 := generateTestKeypair(t)
	_, priv2 := generateTestKeypair(t)

	manifest := &integrity.Manifest{
		ProductID: "prd_sig",
		Version:   "1.0.0",
		CreatedAt: "2026-10-18T00:00:00Z",
		Files: []integrity.FileEntry{
			{Path: "sample.txt", SHA256: strings.Repeat("0", 64), Size: 10},
		},
	}

	// Signed with priv1
	env, err := integrity.SignManifest(manifest, priv1, "key_1")
	if err != nil {
		t.Fatalf("sign: %v", err)
	}

	// 1. Unknown Key ID
	trustedWithoutKey1 := map[string]ed25519.PublicKey{
		"key_2": pub1,
	}
	_, err = integrity.VerifyEnvelope(env, trustedWithoutKey1)
	if err == nil || !strings.Contains(err.Error(), "unknown signing key") {
		t.Errorf("expected unknown signing key error, got: %v", err)
	}

	// 2. Tampered Payload (modified version inside payload)
	tamperedPayload := base64.RawURLEncoding.EncodeToString([]byte(`{"manifest_version":1,"version":"2.0.0"}`))
	tamperedEnv := *env
	tamperedEnv.Payload = tamperedPayload

	trustedWithKey1 := map[string]ed25519.PublicKey{
		"key_1": pub1,
	}
	_, err = integrity.VerifyEnvelope(&tamperedEnv, trustedWithKey1)
	if err == nil || !strings.Contains(err.Error(), "signature verification failed") {
		t.Errorf("expected signature verification failure, got: %v", err)
	}

	// 3. Signature forged with different private key (priv2)
	envForged, _ := integrity.SignManifest(manifest, priv2, "key_1")
	_, err = integrity.VerifyEnvelope(envForged, trustedWithKey1)
	if err == nil || !strings.Contains(err.Error(), "signature verification failed") {
		t.Errorf("expected forgery failure, got: %v", err)
	}
}

// 6. Key Rotation
func TestIntegrity_KeyRotation(t *testing.T) {
	pubOld, privOld := generateTestKeypair(t)
	pubNew, privNew := generateTestKeypair(t)

	keyIDOld := "key_2025"
	keyIDNew := "key_2026"

	manifestOld := &integrity.Manifest{
		ProductID: "prd_rot",
		Version:   "1.0.0",
		CreatedAt: "2025-01-01T00:00:00Z",
		Files:     []integrity.FileEntry{{Path: "f.txt", SHA256: strings.Repeat("a", 64), Size: 5}},
	}
	manifestNew := &integrity.Manifest{
		ProductID: "prd_rot",
		Version:   "2.0.0",
		CreatedAt: "2026-01-01T00:00:00Z",
		Files:     []integrity.FileEntry{{Path: "f.txt", SHA256: strings.Repeat("b", 64), Size: 5}},
	}

	envOld, _ := integrity.SignManifest(manifestOld, privOld, keyIDOld)
	envNew, _ := integrity.SignManifest(manifestNew, privNew, keyIDNew)

	// Keystore holding both old (retired) and new (active) keys
	keyStore := map[string]ed25519.PublicKey{
		keyIDOld: pubOld,
		keyIDNew: pubNew,
	}

	// Both should verify successfully using their explicit key IDs
	if _, err := integrity.VerifyEnvelope(envOld, keyStore); err != nil {
		t.Errorf("verify old key: %v", err)
	}
	if _, err := integrity.VerifyEnvelope(envNew, keyStore); err != nil {
		t.Errorf("verify new key: %v", err)
	}
}

// 7. Unsupported Manifest Version
func TestIntegrity_UnsupportedVersion(t *testing.T) {
	pub, priv := generateTestKeypair(t)
	keyID := "test_v99"

	env, _ := integrity.SignManifest(&integrity.Manifest{
		ProductID: "prd_test",
		Version:   "1.0.0",
		Files:     []integrity.FileEntry{{Path: "f.txt", SHA256: strings.Repeat("1", 64), Size: 1}},
	}, priv, keyID)

	// Force envelope version to future unsupported version
	env.ManifestVersion = 99
	_, err := integrity.VerifyEnvelope(env, map[string]ed25519.PublicKey{keyID: pub})
	if err == nil || !strings.Contains(err.Error(), "version 99 is not supported") {
		t.Errorf("expected unsupported version error, got %v", err)
	}
}

// 8. Deterministic Output (Bit-for-bit reproducibility)
func TestIntegrity_DeterministicOutput(t *testing.T) {
	_, priv := generateTestKeypair(t)
	keyID := "repro_key"

	// Create two identical manifests with files in reverse order
	m1 := &integrity.Manifest{
		ProductID: "prd_repro",
		Version:   "1.0.0",
		CreatedAt: "2026-10-18T12:00:00Z",
		Files: []integrity.FileEntry{
			{Path: "b/file.txt", SHA256: strings.Repeat("2", 64), Size: 20},
			{Path: "a/file.txt", SHA256: strings.Repeat("1", 64), Size: 10},
		},
	}
	m2 := &integrity.Manifest{
		ProductID: "prd_repro",
		Version:   "1.0.0",
		CreatedAt: "2026-10-18T12:00:00Z",
		Files: []integrity.FileEntry{
			{Path: "a/file.txt", SHA256: strings.Repeat("1", 64), Size: 10},
			{Path: "b/file.txt", SHA256: strings.Repeat("2", 64), Size: 20},
		},
	}

	c1, err1 := integrity.Canonicalize(m1)
	c2, err2 := integrity.Canonicalize(m2)
	if err1 != nil || err2 != nil {
		t.Fatalf("canonicalize errors: %v, %v", err1, err2)
	}
	if string(c1) != string(c2) {
		t.Errorf("canonicalization not deterministic:\n%s\nvs\n%s", string(c1), string(c2))
	}

	env1, err1 := integrity.SignManifest(m1, priv, keyID)
	env2, err2 := integrity.SignManifest(m2, priv, keyID)
	if err1 != nil || err2 != nil {
		t.Fatalf("sign errors: %v, %v", err1, err2)
	}

	if env1.Payload != env2.Payload {
		t.Errorf("payload mismatch in identical runs")
	}
	if env1.Signature != env2.Signature {
		t.Errorf("signature mismatch in deterministic Ed25519 runs")
	}
}
