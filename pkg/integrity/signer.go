package integrity

import (
	"crypto/ed25519"
	"encoding/base64"
	"errors"
	"fmt"
)

// SignManifest serializes the manifest deterministically, signs its base64url payload with
// the supplied Ed25519 private key, and returns the signed envelope.
//
// The private key remains outside distributed applications and build artifacts.
func SignManifest(m *Manifest, privKey ed25519.PrivateKey, keyID string) (*SignedEnvelope, error) {
	if m == nil {
		return nil, errors.New("manifest cannot be nil")
	}
	if len(privKey) != ed25519.PrivateKeySize {
		return nil, fmt.Errorf("invalid ed25519 private key size: expected %d bytes, got %d",
			ed25519.PrivateKeySize, len(privKey))
	}
	if keyID == "" {
		return nil, errors.New("key_id is required")
	}

	m.SigningKeyID = keyID
	m.ManifestVersion = CurrentManifestVersion

	canonicalBytes, err := Canonicalize(m)
	if err != nil {
		return nil, fmt.Errorf("canonicalize manifest: %w", err)
	}

	// Payload is base64url unpadded (RFC 4648 §5), matching Keygate token convention
	payloadB64 := base64.RawURLEncoding.EncodeToString(canonicalBytes)

	// Ed25519 signature over the payload string bytes
	sig := ed25519.Sign(privKey, []byte(payloadB64))
	sigB64 := base64.RawURLEncoding.EncodeToString(sig)

	// Self-verification check before returning to guarantee zero corrupted signatures
	pubKey := privKey.Public().(ed25519.PublicKey)
	if !ed25519.Verify(pubKey, []byte(payloadB64), sig) {
		return nil, errors.New("internal error: self-verification of newly generated signature failed")
	}

	env := &SignedEnvelope{
		Format:          FormatIdentifier,
		ManifestVersion: CurrentManifestVersion,
		Payload:         payloadB64,
		Signature:       sigB64,
		SigningKeyID:    keyID,
		Algorithm:       AlgorithmEd25519,
		Manifest:        m,
	}

	return env, nil
}
