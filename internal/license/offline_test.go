package license

import (
	"crypto/ed25519"
	"errors"
	"testing"
	"time"
)

func TestVerifyWithOptions_Valid(t *testing.T) {
	priv := testKey(t)
	pub := PublicKey(priv)

	now := time.Now()
	tok := &VerifyToken{
		Version:    "1",
		KeyID:      KeyIDFromPublicKey(pub),
		LicenseID:  "lic-100",
		ProductID:  "prod-abc",
		PlanID:     "plan-pro",
		Status:     "active",
		Identifier: "device-1",
		Features:   map[string]any{"export": true, "offline_mode": true},
		IssuedAt:   now.Add(-time.Hour).Unix(),
		ExpiresAt:  now.Add(24 * time.Hour).Unix(),
		ValidUntil: now.Add(365 * 24 * time.Hour).Unix(),
		GraceDays:  7,
	}

	signed, err := Sign(tok, priv)
	if err != nil {
		t.Fatalf("Sign: %v", err)
	}

	parsed, err := VerifyWithOptions(signed, VerifyOptions{
		PublicKey:          pub,
		ExpectedProductID:  "prod-abc",
		ExpectedIdentifier: "device-1",
		RequiredFeatures:   []string{"export", "offline_mode"},
		Now:                now,
	})
	if err != nil {
		t.Fatalf("VerifyWithOptions: %v", err)
	}
	if parsed.LicenseID != "lic-100" {
		t.Errorf("LicenseID mismatch: %s", parsed.LicenseID)
	}
	if parsed.Version != "1" {
		t.Errorf("Version mismatch: %s", parsed.Version)
	}
	if parsed.KeyID != KeyIDFromPublicKey(pub) {
		t.Errorf("KeyID mismatch: %s", parsed.KeyID)
	}
}

func TestVerifyWithOptions_ClockSkewAndExpiration(t *testing.T) {
	priv := testKey(t)
	pub := PublicKey(priv)

	now := time.Now()
	// Token expired 30 seconds ago
	tok := &VerifyToken{
		LicenseID: "lic-200",
		ProductID: "prod-abc",
		Status:    "active",
		IssuedAt:  now.Add(-time.Hour).Unix(),
		ExpiresAt: now.Add(-30 * time.Second).Unix(),
	}

	signed, err := Sign(tok, priv)
	if err != nil {
		t.Fatalf("Sign: %v", err)
	}

	// Without clock skew -> must fail with ErrTokenExpired
	_, err = VerifyWithOptions(signed, VerifyOptions{
		PublicKey: pub,
		Now:       now,
		ClockSkew: 0,
	})
	if !errors.Is(err, ErrTokenExpired) {
		t.Fatalf("expected ErrTokenExpired, got: %v", err)
	}

	// With 60-second clock skew tolerance -> must pass
	_, err = VerifyWithOptions(signed, VerifyOptions{
		PublicKey: pub,
		Now:       now,
		ClockSkew: 60 * time.Second,
	})
	if err != nil {
		t.Fatalf("expected pass with clock skew, got: %v", err)
	}
}

func TestVerifyWithOptions_LicenseTermAndGracePeriod(t *testing.T) {
	priv := testKey(t)
	pub := PublicKey(priv)

	now := time.Now()
	// License term ended 5 days ago, grace period is 7 days
	tokInGrace := &VerifyToken{
		LicenseID:  "lic-300",
		ProductID:  "prod-abc",
		Status:     "active",
		IssuedAt:   now.Add(-24 * time.Hour).Unix(),
		ExpiresAt:  now.Add(24 * time.Hour).Unix(),
		ValidUntil: now.Add(-5 * 24 * time.Hour).Unix(),
		GraceDays:  7,
	}
	signedGrace, err := Sign(tokInGrace, priv)
	if err != nil {
		t.Fatalf("Sign: %v", err)
	}

	_, err = VerifyWithOptions(signedGrace, VerifyOptions{
		PublicKey: pub,
		Now:       now,
	})
	if err != nil {
		t.Fatalf("token within grace days should pass, got: %v", err)
	}

	// License term ended 10 days ago with 7 grace days -> expired
	tokLapsed := &VerifyToken{
		LicenseID:  "lic-301",
		ProductID:  "prod-abc",
		Status:     "active",
		IssuedAt:   now.Add(-24 * time.Hour).Unix(),
		ExpiresAt:  now.Add(24 * time.Hour).Unix(),
		ValidUntil: now.Add(-10 * 24 * time.Hour).Unix(),
		GraceDays:  7,
	}
	signedLapsed, err := Sign(tokLapsed, priv)
	if err != nil {
		t.Fatalf("Sign: %v", err)
	}

	_, err = VerifyWithOptions(signedLapsed, VerifyOptions{
		PublicKey: pub,
		Now:       now,
	})
	if !errors.Is(err, ErrLicenseTermExpired) {
		t.Fatalf("expected ErrLicenseTermExpired, got: %v", err)
	}
}

func TestVerifyWithOptions_KeyRotationAndKeyring(t *testing.T) {
	priv1 := testKey(t)
	pub1 := PublicKey(priv1)
	kid1 := KeyIDFromPublicKey(pub1)

	priv2 := testKey(t)
	pub2 := PublicKey(priv2)
	kid2 := KeyIDFromPublicKey(pub2)

	keyring := map[string]ed25519.PublicKey{
		kid1: pub1,
		kid2: pub2,
	}

	now := time.Now()
	// Token signed with key 2 (rotated)
	tok := &VerifyToken{
		KeyID:     kid2,
		LicenseID: "lic-400",
		ProductID: "prod-abc",
		Status:    "active",
		IssuedAt:  now.Unix(),
		ExpiresAt: now.Add(24 * time.Hour).Unix(),
	}
	signed, err := Sign(tok, priv2)
	if err != nil {
		t.Fatalf("Sign: %v", err)
	}

	parsed, err := VerifyWithKeyring(signed, keyring)
	if err != nil {
		t.Fatalf("VerifyWithKeyring failed: %v", err)
	}
	if parsed.KeyID != kid2 {
		t.Errorf("KeyID mismatch: got %s, want %s", parsed.KeyID, kid2)
	}

	// Token signed with untrusted key
	untrustedPriv := testKey(t)
	untrustedPub := PublicKey(untrustedPriv)
	untrustedKid := KeyIDFromPublicKey(untrustedPub)

	untrustedTok := &VerifyToken{
		KeyID:     untrustedKid,
		LicenseID: "lic-untrusted",
		ProductID: "prod-abc",
		Status:    "active",
		IssuedAt:  now.Unix(),
		ExpiresAt: now.Add(24 * time.Hour).Unix(),
	}
	signedUntrusted, err := Sign(untrustedTok, untrustedPriv)
	if err != nil {
		t.Fatalf("Sign: %v", err)
	}

	_, err = VerifyWithKeyring(signedUntrusted, keyring)
	if !errors.Is(err, ErrUnknownSigningKey) {
		t.Fatalf("expected ErrUnknownSigningKey, got: %v", err)
	}
}

func TestVerifyWithOptions_ProductAndDeviceMismatch(t *testing.T) {
	priv := testKey(t)
	pub := PublicKey(priv)

	now := time.Now()
	tok := &VerifyToken{
		LicenseID:  "lic-500",
		ProductID:  "prod-expected",
		Identifier: "device-expected",
		Status:     "active",
		IssuedAt:   now.Unix(),
		ExpiresAt:  now.Add(24 * time.Hour).Unix(),
	}
	signed, err := Sign(tok, priv)
	if err != nil {
		t.Fatalf("Sign: %v", err)
	}

	// Product mismatch
	_, err = VerifyWithOptions(signed, VerifyOptions{
		PublicKey:         pub,
		ExpectedProductID: "prod-wrong",
		Now:               now,
	})
	if !errors.Is(err, ErrProductIDMismatch) {
		t.Fatalf("expected ErrProductIDMismatch, got: %v", err)
	}

	// Device mismatch
	_, err = VerifyWithOptions(signed, VerifyOptions{
		PublicKey:          pub,
		ExpectedIdentifier: "device-wrong",
		Now:                now,
	})
	if !errors.Is(err, ErrDeviceMismatch) {
		t.Fatalf("expected ErrDeviceMismatch, got: %v", err)
	}
}

func TestVerifyWithOptions_MissingEntitlement(t *testing.T) {
	priv := testKey(t)
	pub := PublicKey(priv)

	now := time.Now()
	tok := &VerifyToken{
		LicenseID: "lic-600",
		ProductID: "prod-abc",
		Status:    "active",
		Features:  map[string]any{"basic_tier": true},
		IssuedAt:  now.Unix(),
		ExpiresAt: now.Add(24 * time.Hour).Unix(),
	}
	signed, err := Sign(tok, priv)
	if err != nil {
		t.Fatalf("Sign: %v", err)
	}

	_, err = VerifyWithOptions(signed, VerifyOptions{
		PublicKey:        pub,
		RequiredFeatures: []string{"advanced_reports"},
		Now:              now,
	})
	if !errors.Is(err, ErrMissingEntitlement) {
		t.Fatalf("expected ErrMissingEntitlement, got: %v", err)
	}
}

func TestVerifyWithOptions_UnsupportedVersion(t *testing.T) {
	priv := testKey(t)
	pub := PublicKey(priv)

	now := time.Now()
	tok := &VerifyToken{
		Version:   "99", // future unknown major version
		LicenseID: "lic-700",
		ProductID: "prod-abc",
		Status:    "active",
		IssuedAt:  now.Unix(),
		ExpiresAt: now.Add(24 * time.Hour).Unix(),
	}
	signed, err := Sign(tok, priv)
	if err != nil {
		t.Fatalf("Sign: %v", err)
	}

	_, err = VerifyWithOptions(signed, VerifyOptions{
		PublicKey: pub,
		Now:       now,
	})
	if !errors.Is(err, ErrUnsupportedVersion) {
		t.Fatalf("expected ErrUnsupportedVersion, got: %v", err)
	}
}

func TestVerifyWithOptions_LegacyToken(t *testing.T) {
	priv := testKey(t)
	pub := PublicKey(priv)

	now := time.Now()
	// Legacy token without Version or KeyID set
	tok := &VerifyToken{
		LicenseID: "lic-legacy",
		ProductID: "prod-abc",
		Status:    "active",
		IssuedAt:  now.Unix(),
		ExpiresAt: now.Add(24 * time.Hour).Unix(),
	}
	// Direct signing
	signed, err := Sign(tok, priv)
	if err != nil {
		t.Fatalf("Sign: %v", err)
	}

	// Verify using standard legacy Verify(raw, pub)
	parsed, err := Verify(signed, pub)
	if err != nil {
		t.Fatalf("Verify legacy token failed: %v", err)
	}
	if parsed.LicenseID != "lic-legacy" {
		t.Errorf("LicenseID mismatch: %s", parsed.LicenseID)
	}
}
