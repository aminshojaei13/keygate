package license

import (
	"crypto/ed25519"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"strings"
	"time"
)

// VerifyToken is the offline-verifiable license payload returned by
// /license/activate and /license/verify.
//
// Signing: Ed25519. The server holds the private key; clients fetch
// the matching public key once (GET /api/v1/license/pubkey) and use
// it to verify tokens locally — no network call needed during the
// validity window.
//
// We intentionally do NOT use HMAC. HMAC is symmetric: a client that
// can verify can also sign. Shipping the shared secret inside a
// desktop binary makes it extractable, which lets an attacker forge
// arbitrary license tokens. Ed25519 puts the signing capability
// behind a key that never leaves the server.
type VerifyToken struct {
	// Version is the token format specification version (e.g. "1").
	Version string `json:"ver,omitempty"`
	// KeyID identifies the public key authorized to verify this token.
	KeyID string `json:"kid,omitempty"`

	LicenseID  string         `json:"lid"`
	ProductID  string         `json:"pid"`
	PlanID     string         `json:"pln"`
	Status     string         `json:"sts"`
	Identifier string         `json:"did"`
	Features   map[string]any `json:"ftr,omitempty"`
	IssuedAt   int64          `json:"iat"`
	// ExpiresAt is the token's own lifetime — how long a client may
	// stay offline before it must check in again. It is NOT the
	// license's end date; see ValidUntil.
	ExpiresAt int64 `json:"exp"`
	// ValidUntil is when the license itself lapses, as a unix second.
	// Zero (field omitted) means perpetual.
	//
	// It has to be inside the signed payload: it used to travel only
	// in the surrounding JSON envelope, which is unsigned, so an
	// offline client had no way to tell a real end date from one a
	// proxy rewrote on the way. exp alone cannot stand in for it —
	// exp is a check-in interval and says nothing about the licence
	// term.
	ValidUntil int64 `json:"vun,omitempty"`
	// UpdatesUntil ends the maintenance period of a perpetual license,
	// as a unix second: a client may install releases published up to
	// this instant and must refuse newer ones. Zero (field omitted)
	// means no separate limit. Signed for the same reason as vun.
	UpdatesUntil int64  `json:"upd,omitempty"`
	GraceDays    int    `json:"grc"`
	Nonce        string `json:"nce"`           // unique per-issuance to prevent replay
	Fingerprint  string `json:"fpr,omitempty"` // SHA256(identifier+product_id) for binding
}

// KeyIDFromPublicKey computes a deterministic identifier for an Ed25519 public key.
// It uses a standard SHA-256 digest truncated to 8 bytes (16 hex chars) with a "key_" prefix.
func KeyIDFromPublicKey(pub ed25519.PublicKey) string {
	h := sha256.Sum256(pub)
	return "key_" + hex.EncodeToString(h[:8])
}

// Standard errors for token verification.
var (
	ErrInvalidFormat       = fmt.Errorf("invalid token format")
	ErrSignatureDecode     = fmt.Errorf("signature decode error")
	ErrPayloadDecode       = fmt.Errorf("payload decode error")
	ErrPayloadUnmarshal    = fmt.Errorf("payload unmarshal error")
	ErrInvalidSignature    = fmt.Errorf("invalid signature")
	ErrKeyNotInitialised   = fmt.Errorf("ed25519 public key not initialised")
	ErrTokenExpired        = fmt.Errorf("token expired")
	ErrLicenseTermExpired  = fmt.Errorf("license term expired")
	ErrTokenIssuedInFuture = fmt.Errorf("token issued in the future")
	ErrUnsupportedVersion  = fmt.Errorf("unsupported token version")
	ErrUnknownSigningKey   = fmt.Errorf("unknown signing key")
	ErrProductIDMismatch   = fmt.Errorf("product ID mismatch")
	ErrDeviceMismatch      = fmt.Errorf("device identifier mismatch")
	ErrFingerprintMismatch = fmt.Errorf("device fingerprint mismatch")
	ErrMissingEntitlement  = fmt.Errorf("missing required entitlement")
	ErrDurationExceeded    = fmt.Errorf("token offline duration exceeds permitted limit")
)

// VerifyOptions controls token validation parameters.
type VerifyOptions struct {
	// PublicKey is the single public key expected to have signed the token.
	PublicKey ed25519.PublicKey
	// Keyring provides a map of known KeyID -> PublicKey for multi-key verification / rotation.
	Keyring map[string]ed25519.PublicKey
	// ClockSkew is the maximum permitted leeway for expiration timestamps (default: 0).
	ClockSkew time.Duration
	// Now is the reference time for validation (defaults to time.Now() if zero).
	Now time.Time
	// ExpectedProductID validates that token.ProductID matches when non-empty.
	ExpectedProductID string
	// ExpectedIdentifier validates that token.Identifier and token.Fingerprint match when non-empty.
	ExpectedIdentifier string
	// RequiredFeatures checks that each feature listed is present and truthy in token.Features.
	RequiredFeatures []string
	// MaxOfflineDuration optionally rejects tokens where (exp - iat) exceeds a maximum permitted window.
	MaxOfflineDuration time.Duration
}

// PrivateKeyFromHex parses a 32-byte ed25519 seed (64 hex chars) and
// derives the full private key. The seed is what operators rotate
// via their secret manager — it's short enough to fit in a single
// env var, unlike a full 64-byte raw private key.
//
// Generate one with: openssl rand -hex 32
func PrivateKeyFromHex(s string) (ed25519.PrivateKey, error) {
	seed, err := hex.DecodeString(strings.TrimSpace(s))
	if err != nil {
		return nil, fmt.Errorf("must be hex-encoded: %w", err)
	}
	if len(seed) != ed25519.SeedSize {
		return nil, fmt.Errorf("must decode to %d bytes (got %d); generate with 'openssl rand -hex 32'",
			ed25519.SeedSize, len(seed))
	}
	return ed25519.NewKeyFromSeed(seed), nil
}

// PublicKey returns the verify key derived from the same seed. Used
// to serve the /license/pubkey endpoint without re-parsing the seed.
func PublicKey(priv ed25519.PrivateKey) ed25519.PublicKey {
	return priv.Public().(ed25519.PublicKey)
}

// Sign produces base64url(payload).base64url(signature). The signed
// region is the base64-encoded payload, NOT the raw JSON, so clients
// can verify by base64-decoding the signature half and running the
// payload-half bytes through ed25519.Verify.
func Sign(t *VerifyToken, priv ed25519.PrivateKey) (string, error) {
	if len(priv) != ed25519.PrivateKeySize {
		return "", fmt.Errorf("ed25519 private key not initialised")
	}
	if t.Version == "" {
		t.Version = "1"
	}
	if t.KeyID == "" {
		t.KeyID = KeyIDFromPublicKey(PublicKey(priv))
	}
	if t.Nonce == "" {
		nonce := make([]byte, 16)
		if _, err := rand.Read(nonce); err != nil {
			return "", fmt.Errorf("nonce: %w", err)
		}
		t.Nonce = base64.RawURLEncoding.EncodeToString(nonce)
	}
	payload, err := json.Marshal(t)
	if err != nil {
		return "", fmt.Errorf("marshal: %w", err)
	}
	b64 := base64.RawURLEncoding.EncodeToString(payload)
	sig := ed25519.Sign(priv, []byte(b64))
	return b64 + "." + base64.RawURLEncoding.EncodeToString(sig), nil
}

// Verify checks the signature with the server's public key and
// returns the decoded payload. ExpiresAt is checked here so callers
// don't accidentally trust stale tokens.
func Verify(raw string, pub ed25519.PublicKey) (*VerifyToken, error) {
	return VerifyWithOptions(raw, VerifyOptions{
		PublicKey: pub,
	})
}

// VerifyWithKeyring checks the signature against a map of KeyID -> PublicKey.
func VerifyWithKeyring(raw string, keyring map[string]ed25519.PublicKey) (*VerifyToken, error) {
	return VerifyWithOptions(raw, VerifyOptions{
		Keyring: keyring,
	})
}

// VerifyWithOptions performs comprehensive validation against an options struct.
func VerifyWithOptions(raw string, opts VerifyOptions) (*VerifyToken, error) {
	idx := strings.LastIndexByte(raw, '.')
	if idx < 0 || strings.Count(raw, ".") != 1 {
		return nil, ErrInvalidFormat
	}
	b64, sigB64 := raw[:idx], raw[idx+1:]
	if b64 == "" || sigB64 == "" {
		return nil, ErrInvalidFormat
	}

	sig, err := base64.RawURLEncoding.DecodeString(sigB64)
	if err != nil || len(sig) != ed25519.SignatureSize {
		return nil, ErrInvalidSignature
	}

	payload, err := base64.RawURLEncoding.DecodeString(b64)
	if err != nil {
		return nil, fmt.Errorf("%w: %v", ErrPayloadDecode, err)
	}
	var t VerifyToken
	if err := json.Unmarshal(payload, &t); err != nil {
		return nil, fmt.Errorf("%w: %v", ErrPayloadUnmarshal, err)
	}

	// Version check: empty/missing is v1 (backward compatibility)
	if t.Version != "" && t.Version != "1" {
		return nil, fmt.Errorf("%w: %s", ErrUnsupportedVersion, t.Version)
	}

	// Resolve public key
	var pub ed25519.PublicKey
	if len(opts.PublicKey) == ed25519.PublicKeySize {
		pub = opts.PublicKey
	} else if len(opts.Keyring) > 0 {
		if t.KeyID != "" {
			k, ok := opts.Keyring[t.KeyID]
			if !ok {
				return nil, fmt.Errorf("%w: %s", ErrUnknownSigningKey, t.KeyID)
			}
			pub = k
		} else if len(opts.Keyring) == 1 {
			// Single key in keyring fallback for legacy tokens without kid
			for _, k := range opts.Keyring {
				pub = k
				break
			}
		} else {
			return nil, fmt.Errorf("%w: token missing key ID", ErrUnknownSigningKey)
		}
	}

	if len(pub) != ed25519.PublicKeySize {
		return nil, ErrKeyNotInitialised
	}

	if !ed25519.Verify(pub, []byte(b64), sig) {
		return nil, ErrInvalidSignature
	}

	refTime := opts.Now
	if refTime.IsZero() {
		refTime = time.Now()
	}

	skew := opts.ClockSkew

	if opts.MaxOfflineDuration > 0 && t.IssuedAt > 0 && t.ExpiresAt > 0 {
		dur := time.Duration(t.ExpiresAt-t.IssuedAt) * time.Second
		if dur > opts.MaxOfflineDuration {
			return nil, ErrDurationExceeded
		}
	}

	if t.IssuedAt > 0 && t.IssuedAt > refTime.Add(skew).Unix() {
		return nil, ErrTokenIssuedInFuture
	}

	if t.ExpiresAt > 0 && refTime.Add(-skew).Unix() > t.ExpiresAt {
		return nil, ErrTokenExpired
	}

	if t.ValidUntil > 0 {
		deadline := time.Unix(t.ValidUntil, 0).Add(time.Duration(t.GraceDays) * 24 * time.Hour)
		if refTime.Add(-skew).After(deadline) {
			return nil, ErrLicenseTermExpired
		}
	}

	if opts.ExpectedProductID != "" && t.ProductID != opts.ExpectedProductID {
		return nil, fmt.Errorf("%w: got %s, want %s", ErrProductIDMismatch, t.ProductID, opts.ExpectedProductID)
	}

	if opts.ExpectedIdentifier != "" {
		if t.Identifier != "" && t.Identifier != opts.ExpectedIdentifier {
			return nil, fmt.Errorf("%w: got %s, want %s", ErrDeviceMismatch, t.Identifier, opts.ExpectedIdentifier)
		}
		if t.Fingerprint != "" {
			expectedFpr := Fingerprint(opts.ExpectedIdentifier, t.ProductID)
			if t.Fingerprint != expectedFpr {
				return nil, fmt.Errorf("%w: got %s, want %s", ErrFingerprintMismatch, t.Fingerprint, expectedFpr)
			}
		}
	}

	for _, reqFeat := range opts.RequiredFeatures {
		val, exists := t.Features[reqFeat]
		if !exists || val == nil || val == false {
			return nil, fmt.Errorf("%w: %s", ErrMissingEntitlement, reqFeat)
		}
	}

	return &t, nil
}

// Fingerprint binds a token to the (identifier, product_id) pair so
// a token leaked from one device can't be replayed on a sibling
// machine that knows the license key. SHA256 truncated to 8 bytes
// keeps the token small while staying collision-resistant for this
// scope (we only need uniqueness within one customer's devices).
func Fingerprint(identifier, productID string) string {
	h := sha256.Sum256([]byte(identifier + ":" + productID))
	return hex.EncodeToString(h[:8])
}
