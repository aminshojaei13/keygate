package deviceid_test

import (
	"crypto/ed25519"
	"crypto/rand"
	"encoding/base64"
	"encoding/hex"
	"fmt"
	"strings"
	"testing"

	"github.com/tabloy/keygate/pkg/deviceid"
)

// 1. Stable Identifiers
func TestDeviceID_StableIdentifiers(t *testing.T) {
	platform := deviceid.PlatformDarwin
	rawComponents := map[string]string{
		deviceid.ComponentBoard: "F8A9B2C3-4D5E-6F70-8192-A3B4C5D6E7F8",
		deviceid.ComponentOS:    "24A348",
		deviceid.ComponentDisk:  "DISK-UUID-00112233-4455-6677",
		deviceid.ComponentCPU:   "Apple M3 Pro (12 cores)",
		deviceid.ComponentNIC:   "3c:22:fb:a1:b2:c3",
	}

	compHashes := make(map[string]string)
	for k, v := range rawComponents {
		compHashes[k] = deviceid.HashComponent(k, v)
		if len(compHashes[k]) != 64 {
			t.Fatalf("expected 64-hex component hash for %s, got %d", k, len(compHashes[k]))
		}
	}

	digest1, err := deviceid.ComputeCompositeDigest(platform, compHashes)
	if err != nil {
		t.Fatalf("compute digest 1: %v", err)
	}

	// Repeated computation with same inputs must yield identical digest
	digest2, err := deviceid.ComputeCompositeDigest(platform, compHashes)
	if err != nil {
		t.Fatalf("compute digest 2: %v", err)
	}

	if digest1 != digest2 {
		t.Errorf("digest changed between identical runs: %s != %s", digest1, digest2)
	}

	kgid := deviceid.FormatKGID(platform, digest1)
	if err := deviceid.ValidateIdentifierFormat(kgid); err != nil {
		t.Errorf("expected valid kgid format: %v", err)
	}

	scheme, parsedPlat, parsedDigest, err := deviceid.ParseIdentifier(kgid)
	if err != nil {
		t.Fatalf("parse kgid: %v", err)
	}
	if scheme != deviceid.SchemeKGIDv1 {
		t.Errorf("scheme = %q, want %q", scheme, deviceid.SchemeKGIDv1)
	}
	if parsedPlat != platform {
		t.Errorf("platform = %q, want %q", parsedPlat, platform)
	}
	if parsedDigest != digest1 {
		t.Errorf("parsed digest = %q, want %q", parsedDigest, digest1)
	}
}

// 2. Missing Characteristics (e.g. mobile sandbox / browser permissions restricted)
func TestDeviceID_MissingCharacteristics(t *testing.T) {
	// Baseline: full characteristics
	baseline := map[string]string{
		deviceid.ComponentBoard: deviceid.HashComponent(deviceid.ComponentBoard, "SYNTHETIC-BOARD-1"),
		deviceid.ComponentOS:    deviceid.HashComponent(deviceid.ComponentOS, "SYNTHETIC-OS-1"),
		deviceid.ComponentDisk:  deviceid.HashComponent(deviceid.ComponentDisk, "SYNTHETIC-DISK-1"),
		deviceid.ComponentCPU:   deviceid.HashComponent(deviceid.ComponentCPU, "SYNTHETIC-CPU-1"),
		deviceid.ComponentNIC:   deviceid.HashComponent(deviceid.ComponentNIC, "aa:bb:cc:dd:ee:01"),
	}

	// Incoming: restricted environment missing NIC and Disk (e.g. sandboxed app or privacy restriction)
	incoming := map[string]string{
		deviceid.ComponentBoard: baseline[deviceid.ComponentBoard],
		deviceid.ComponentOS:    baseline[deviceid.ComponentOS],
		deviceid.ComponentCPU:   baseline[deviceid.ComponentCPU],
	}

	res := deviceid.EvaluateDrift(baseline, incoming)
	if len(res.MissingComponents) != 2 {
		t.Errorf("expected 2 missing components, got %v", res.MissingComponents)
	}
	// With Board(35)+OS(25)+CPU(10) matching out of 70 comparable + 30/2 missing = 70/85 = 82%
	if res.Decision != deviceid.DecisionAcceptSameDevice {
		t.Errorf("expected AcceptSameDevice despite missing components, got %s (%d)", res.Decision, res.Score)
	}
}

// 3. Changed Characteristics (Single component replacements vs total changes)
func TestDeviceID_ChangedCharacteristics(t *testing.T) {
	baseRaw := map[string]string{
		deviceid.ComponentBoard: "BOARD-AAA",
		deviceid.ComponentOS:    "OS-10.0.1",
		deviceid.ComponentDisk:  "DISK-NVME-001",
		deviceid.ComponentCPU:   "CPU-X86-8CORE",
		deviceid.ComponentNIC:   "00:11:22:33:44:55",
	}

	baseline := make(map[string]string)
	for k, v := range baseRaw {
		baseline[k] = deviceid.HashComponent(k, v)
	}

	cases := []struct {
		name         string
		modify       func(m map[string]string)
		wantDecision string
		minScore     int
		maxScore     int
	}{
		{
			name: "NIC change (e.g. switched from Ethernet to Wi-Fi adapter)",
			modify: func(m map[string]string) {
				m[deviceid.ComponentNIC] = deviceid.HashComponent(deviceid.ComponentNIC, "99:88:77:66:55:44")
			},
			wantDecision: deviceid.DecisionAcceptSameDevice,
			minScore:     85,
			maxScore:     95,
		},
		{
			name: "Disk replacement (e.g. SSD upgrade)",
			modify: func(m map[string]string) {
				m[deviceid.ComponentDisk] = deviceid.HashComponent(deviceid.ComponentDisk, "DISK-NEW-SAMSUNG-990")
			},
			wantDecision: deviceid.DecisionAcceptSameDevice,
			minScore:     75,
			maxScore:     85,
		},
		{
			name: "OS reinstall (new machine-id/installation UUID)",
			modify: func(m map[string]string) {
				m[deviceid.ComponentOS] = deviceid.HashComponent(deviceid.ComponentOS, "OS-REINSTALLED-GUID-999")
			},
			wantDecision: deviceid.DecisionAcceptSameDevice,
			minScore:     70,
			maxScore:     80,
		},
		{
			name: "Motherboard replacement only",
			modify: func(m map[string]string) {
				m[deviceid.ComponentBoard] = deviceid.HashComponent(deviceid.ComponentBoard, "BOARD-NEW-REPLACEMENT")
			},
			wantDecision: deviceid.DecisionAcceptSameDevice,
			minScore:     60,
			maxScore:     70,
		},
		{
			name: "Motherboard AND Disk replacement (Ambiguous drift)",
			modify: func(m map[string]string) {
				m[deviceid.ComponentBoard] = deviceid.HashComponent(deviceid.ComponentBoard, "BOARD-NEW-REPLACEMENT")
				m[deviceid.ComponentDisk] = deviceid.HashComponent(deviceid.ComponentDisk, "DISK-NEW-SAMSUNG-990")
			},
			wantDecision: deviceid.DecisionReviewAmbiguous,
			minScore:     35,
			maxScore:     55,
		},
		{
			name: "Entire PC changed (all components different)",
			modify: func(m map[string]string) {
				m[deviceid.ComponentBoard] = deviceid.HashComponent(deviceid.ComponentBoard, "TOTALLY-DIFF-BOARD")
				m[deviceid.ComponentOS] = deviceid.HashComponent(deviceid.ComponentOS, "TOTALLY-DIFF-OS")
				m[deviceid.ComponentDisk] = deviceid.HashComponent(deviceid.ComponentDisk, "TOTALLY-DIFF-DISK")
				m[deviceid.ComponentCPU] = deviceid.HashComponent(deviceid.ComponentCPU, "TOTALLY-DIFF-CPU")
				m[deviceid.ComponentNIC] = deviceid.HashComponent(deviceid.ComponentNIC, "fe:ed:fa:ce:ca:fe")
			},
			wantDecision: deviceid.DecisionRejectDifferentDevice,
			minScore:     0,
			maxScore:     10,
		},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			current := make(map[string]string)
			for k, v := range baseline {
				current[k] = v
			}
			tc.modify(current)

			res := deviceid.EvaluateDrift(baseline, current)
			if res.Decision != tc.wantDecision {
				t.Errorf("decision = %q, want %q (score: %d, explanation: %s)",
					res.Decision, tc.wantDecision, res.Score, res.Explanation)
			}
			if res.Score < tc.minScore || res.Score > tc.maxScore {
				t.Errorf("score = %d, want between %d and %d", res.Score, tc.minScore, tc.maxScore)
			}
		})
	}
}

// 4. Version Migration Parsing and Detection
func TestDeviceID_VersionMigration(t *testing.T) {
	legacyOpaque := "macbook-pro-m2-amin"
	uuidV4 := "550e8400-e29b-41d4-a716-446655440000"
	sha256V1 := "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
	kgidV1 := "kgid:v1:windows:9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"

	tests := []struct {
		id         string
		wantScheme string
		wantPlat   string
	}{
		{legacyOpaque, deviceid.SchemeOpaque, ""},
		{uuidV4, deviceid.SchemeUUID, ""},
		{sha256V1, deviceid.SchemeSHA256, ""},
		{kgidV1, deviceid.SchemeKGIDv1, "windows"},
	}

	for _, tt := range tests {
		scheme, plat, _, err := deviceid.ParseIdentifier(tt.id)
		if err != nil {
			t.Errorf("parse %q: %v", tt.id, err)
		}
		if scheme != tt.wantScheme {
			t.Errorf("for %q, scheme = %q, want %q", tt.id, scheme, tt.wantScheme)
		}
		if plat != tt.wantPlat {
			t.Errorf("for %q, platform = %q, want %q", tt.id, plat, tt.wantPlat)
		}
	}
}

// 5. Invalid Identifier Formats
func TestDeviceID_InvalidIdentifierFormats(t *testing.T) {
	invalids := []struct {
		name string
		id   string
	}{
		{"empty string", ""},
		{"whitespace only", "    "},
		{"contains space", "kgid:v1:darwin: bad hex with spaces"},
		{"contains newline", "device-id\n"},
		{"contains null byte", "device\x00id"},
		{"malformed kgid prefix", "kgid:v2:linux:9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"},
		{"kgid invalid platform", "kgid:v1:solaris:9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"},
		{"kgid short hex", "kgid:v1:darwin:abcd1234"},
		{"sha256 short hex", "sha256:abcd"},
		{"sha256 invalid characters", "sha256:zzzzc44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"},
		{"prefixed uuid invalid", "uuid:not-a-uuid"},
		{"excessive length", strings.Repeat("a", 257)},
	}

	for _, tc := range invalids {
		t.Run(tc.name, func(t *testing.T) {
			if err := deviceid.ValidateIdentifierFormat(tc.id); err == nil {
				t.Errorf("expected validation error for %q, got nil", tc.id)
			}
		})
	}
}

// 6. Cryptographic Proof of Possession (Optional Hardware-backed key binding)
func TestDeviceID_ProofOfPossession(t *testing.T) {
	pub, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("generate key: %v", err)
	}

	challenge := []byte("keygate:auth:challenge:nonce_12345:timestamp_1795000000")
	sig := ed25519.Sign(priv, challenge)
	sigB64 := base64.RawURLEncoding.EncodeToString(sig)
	pubHex := hex.EncodeToString(pub)

	// Valid proof
	ok, err := deviceid.VerifyProofOfPossession(pubHex, challenge, sigB64)
	if err != nil || !ok {
		t.Fatalf("verify proof failed: ok=%v, err=%v", ok, err)
	}

	// Tampered challenge
	tamperedChallenge := []byte("keygate:auth:challenge:tampered")
	ok, _ = deviceid.VerifyProofOfPossession(pubHex, tamperedChallenge, sigB64)
	if ok {
		t.Error("tampered challenge should fail proof of possession")
	}

	// Wrong key
	pubWrong, _, _ := ed25519.GenerateKey(rand.Reader)
	ok, _ = deviceid.VerifyProofOfPossession(hex.EncodeToString(pubWrong), challenge, sigB64)
	if ok {
		t.Error("wrong public key should fail proof of possession")
	}
}

// 7. Cloned Client State Simulation
func TestDeviceID_ClonedClientState(t *testing.T) {
	// A cloned VM or cloned drive copies the OS machine-id and local anchor UUID,
	// but the underlying Motherboard, CPU, and primary MAC address differ.
	clientAComponents := map[string]string{
		deviceid.ComponentBoard: deviceid.HashComponent(deviceid.ComponentBoard, "MOTHERBOARD-HOST-A"),
		deviceid.ComponentCPU:   deviceid.HashComponent(deviceid.ComponentCPU, "CPU-HOST-A"),
		deviceid.ComponentDisk:  deviceid.HashComponent(deviceid.ComponentDisk, "DISK-CLONED-IMAGE-SERIAL-100"),
		deviceid.ComponentOS:    deviceid.HashComponent(deviceid.ComponentOS, "OS-CLONED-MACHINE-GUID-100"),
		deviceid.ComponentNIC:   deviceid.HashComponent(deviceid.ComponentNIC, "00:50:56:01:02:03"),
	}

	// Clone running on Host B with new virtual NIC, different host CPU and motherboard
	clientBComponents := map[string]string{
		deviceid.ComponentBoard: deviceid.HashComponent(deviceid.ComponentBoard, "MOTHERBOARD-HOST-B"),
		deviceid.ComponentCPU:   deviceid.HashComponent(deviceid.ComponentCPU, "CPU-HOST-B"),
		deviceid.ComponentDisk:  clientAComponents[deviceid.ComponentDisk], // Copied disk image
		deviceid.ComponentOS:    clientAComponents[deviceid.ComponentOS],   // Copied OS state
		deviceid.ComponentNIC:   deviceid.HashComponent(deviceid.ComponentNIC, "00:50:56:04:05:06"),
	}

	res := deviceid.EvaluateDrift(clientAComponents, clientBComponents)
	// Disk (20) + OS (25) = 45 matched, Board (35) + CPU (10) + NIC (10) = 55 changed -> Score 45
	if res.Decision != deviceid.DecisionReviewAmbiguous {
		t.Errorf("clone should be flagged as ambiguous review, got %s (score: %d)", res.Decision, res.Score)
	}
}

// 8. Platform-Specific Failure Handling (Empty/Unreadable characteristics)
func TestDeviceID_PlatformSpecificFailureConditions(t *testing.T) {
	// If a platform returns completely blank or permission-denied values
	emptyHashes := map[string]string{
		deviceid.ComponentBoard: "",
		deviceid.ComponentOS:    "   ",
	}

	_, err := deviceid.ComputeCompositeDigest(deviceid.PlatformLinux, emptyHashes)
	if err == nil {
		t.Error("expected error when all components are empty")
	}

	// If only 1 component is readable (e.g. minimal fallback)
	singleHash := map[string]string{
		deviceid.ComponentOS: deviceid.HashComponent(deviceid.ComponentOS, "fallback-container-uuid"),
	}
	digest, err := deviceid.ComputeCompositeDigest(deviceid.PlatformLinux, singleHash)
	if err != nil {
		t.Fatalf("single fallback component should succeed: %v", err)
	}
	if len(digest) != 64 {
		t.Errorf("expected 64 hex digest, got %d", len(digest))
	}
}
