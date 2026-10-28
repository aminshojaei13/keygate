package handler

import (
	"bytes"
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"github.com/gin-gonic/gin"

	"github.com/tabloy/keygate/internal/model"
	"github.com/tabloy/keygate/internal/service"
	"github.com/tabloy/keygate/internal/store"
	"github.com/tabloy/keygate/pkg/securityevent"
)

type securityHarness struct {
	*testHarness
	secSvc *service.SecurityService
	secH   *SecurityHandler
}

func setupSecurityHarness(t *testing.T) *securityHarness {
	t.Helper()
	h := setupActivationHarness(t, 2)
	logger := slog.New(slog.NewTextHandler(io.Discard, nil))
	svc := service.NewSecurityService(h.store, logger, nil)
	return &securityHarness{
		testHarness: h,
		secSvc:      svc,
		secH:        NewSecurityHandler(svc, h.store),
	}
}

func (h *securityHarness) activateSigned(t *testing.T, lic *model.License, ident string) ed25519.PrivateKey {
	t.Helper()
	pub, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("generate device key: %v", err)
	}
	code, resp := h.postActivate(map[string]any{
		"license_key":       lic.LicenseKey,
		"identifier":        ident,
		"device_public_key": base64.RawURLEncoding.EncodeToString(pub),
		"device_key_alg":    "ed25519",
	})
	if code != http.StatusOK {
		t.Fatalf("activate: %d %v", code, resp)
	}
	return priv
}

func (h *securityHarness) postReport(body map[string]any) (int, map[string]any) {
	b, _ := json.Marshal(body)
	w := httptest.NewRecorder()
	c, _ := gin.CreateTestContext(w)
	c.Request = httptest.NewRequest(http.MethodPost, "/api/v1/license/security-events", bytes.NewReader(b))
	c.Request.Header.Set("Content-Type", "application/json")
	c.Request.RemoteAddr = "198.51.100.10:1234"
	h.secH.Report(c)
	var resp map[string]any
	_ = json.Unmarshal(w.Body.Bytes(), &resp)
	return w.Code, resp
}

func dataOf(t *testing.T, resp map[string]any) map[string]any {
	t.Helper()
	d, ok := resp["data"].(map[string]any)
	if !ok {
		t.Fatalf("missing data envelope: %v", resp)
	}
	return d
}

func (h *securityHarness) signedReport(t *testing.T, lic *model.License, ident, eventID, eventType string, priv ed25519.PrivateKey) map[string]any {
	t.Helper()
	occurred := time.Now().Unix()
	body := map[string]any{
		"license_key": lic.LicenseKey,
		"identifier":  ident,
		"event_id":    eventID,
		"event_type":  eventType,
		"severity":    "high",
		"occurred_at": occurred,
		"details":     map[string]string{"platform": "linux", "affected_files": "1"},
	}
	canonical := securityevent.CanonicalBytes(eventID, lic.ID, ident, eventType, occurred)
	body["signature"] = base64.RawURLEncoding.EncodeToString(ed25519.Sign(priv, canonical))
	return body
}

func TestSecurityEvent_VerifiedReportFlagsNotSuspends(t *testing.T) {
	h := setupSecurityHarness(t)
	lic := h.createLicense(t, "sec1@example.com", model.StatusActive, nil)
	ident := "kgid:v1:linux:" + fmt.Sprintf("%064x", 1)
	priv := h.activateSigned(t, lic, ident)

	code, resp := h.postReport(h.signedReport(t, lic, ident, "evt-1", model.SecurityEventIntegrityViolation, priv))
	if code != http.StatusOK {
		t.Fatalf("report: %d %v", code, resp)
	}
	d := dataOf(t, resp)
	if d["verified"] != true {
		t.Errorf("verified = %v, want true", d["verified"])
	}
	if d["duplicate"] != false {
		t.Errorf("duplicate = %v, want false", d["duplicate"])
	}
	if d["action_taken"] != model.PolicyActionFlag {
		t.Errorf("action = %v, want flag below the threshold", d["action_taken"])
	}
	if d["license_status"] != model.StatusActive {
		t.Errorf("status = %v, want active after a single report", d["license_status"])
	}
	if d["under_review"] != true {
		t.Errorf("under_review = %v, want true", d["under_review"])
	}

	fresh, err := h.store.FindLicenseByID(context.Background(), lic.ID)
	if err != nil {
		t.Fatal(err)
	}
	if fresh.Status != model.StatusActive {
		t.Errorf("db status = %q, want active", fresh.Status)
	}
	if !fresh.UnderReview {
		t.Error("db under_review = false, want true")
	}
}

func TestSecurityEvent_DuplicateIsNoOp(t *testing.T) {
	h := setupSecurityHarness(t)
	lic := h.createLicense(t, "sec-dup@example.com", model.StatusActive, nil)
	ident := "kgid:v1:linux:" + fmt.Sprintf("%064x", 2)
	priv := h.activateSigned(t, lic, ident)

	body := h.signedReport(t, lic, ident, "evt-dup", model.SecurityEventIntegrityViolation, priv)
	if code, resp := h.postReport(body); code != http.StatusOK {
		t.Fatalf("first report: %d %v", code, resp)
	}
	// Send the *same* event three more times: the threshold must not
	// be reached by retries.
	for i := 0; i < 3; i++ {
		code, resp := h.postReport(body)
		if code != http.StatusOK {
			t.Fatalf("duplicate report: %d %v", code, resp)
		}
		d := dataOf(t, resp)
		if d["duplicate"] != true {
			t.Errorf("duplicate = %v, want true", d["duplicate"])
		}
		if d["action_taken"] != nil {
			t.Errorf("duplicate applied action %v", d["action_taken"])
		}
	}
	fresh, _ := h.store.FindLicenseByID(context.Background(), lic.ID)
	if fresh.Status != model.StatusActive {
		t.Errorf("status = %q, want active (duplicates must not accumulate)", fresh.Status)
	}
	events, total, err := h.store.ListSecurityEvents(context.Background(), storeFilter(lic.ID))
	if err != nil {
		t.Fatal(err)
	}
	if total != 1 || len(events) != 1 {
		t.Errorf("stored events = %d, want 1", total)
	}
}

func storeFilter(licenseID string) store.SecurityEventFilter {
	return store.SecurityEventFilter{LicenseID: licenseID, Limit: 100}
}

func TestSecurityEvent_RejectsUnknownLicenseAndDevice(t *testing.T) {
	h := setupSecurityHarness(t)
	lic := h.createLicense(t, "sec-identity@example.com", model.StatusActive, nil)
	ident := "kgid:v1:linux:" + fmt.Sprintf("%064x", 3)
	priv := h.activateSigned(t, lic, ident)

	// A different, real licence key but this licence's device id.
	other := h.createLicense(t, "other@example.com", model.StatusActive, nil)
	body := h.signedReport(t, lic, ident, "evt-cross", model.SecurityEventIntegrityViolation, priv)
	body["license_key"] = other.LicenseKey
	if code, _ := h.postReport(body); code != http.StatusNotFound {
		t.Errorf("cross-license report: %d, want 404", code)
	}

	// Correct key, identifier that was never activated.
	body = h.signedReport(t, lic, ident, "evt-nodevice", model.SecurityEventIntegrityViolation, priv)
	body["identifier"] = "kgid:v1:linux:" + fmt.Sprintf("%064x", 4)
	if code, _ := h.postReport(body); code != http.StatusNotFound {
		t.Errorf("unknown device report: %d, want 404", code)
	}

	events, total, _ := h.store.ListSecurityEvents(context.Background(), storeFilter(lic.ID))
	if total != 0 || len(events) != 0 {
		t.Errorf("stored events = %d, want 0", total)
	}
}

func TestSecurityEvent_RequiresAndVerifiesDeviceSignature(t *testing.T) {
	h := setupSecurityHarness(t)
	lic := h.createLicense(t, "sec-sig@example.com", model.StatusActive, nil)
	ident := "kgid:v1:linux:" + fmt.Sprintf("%064x", 5)
	priv := h.activateSigned(t, lic, ident)

	body := h.signedReport(t, lic, ident, "evt-nosig", model.SecurityEventIntegrityViolation, priv)
	delete(body, "signature")
	if code, resp := h.postReport(body); code != http.StatusUnauthorized {
		t.Fatalf("unsigned report: %d %v, want 401", code, resp)
	}

	// A signature by a different key must not verify.
	_, otherPriv, _ := ed25519.GenerateKey(rand.Reader)
	body = h.signedReport(t, lic, ident, "evt-badsig", model.SecurityEventIntegrityViolation, otherPriv)
	if code, resp := h.postReport(body); code != http.StatusUnauthorized {
		t.Fatalf("bad signature: %d %v, want 401", code, resp)
	}

	events, total, _ := h.store.ListSecurityEvents(context.Background(), storeFilter(lic.ID))
	if total != 0 || len(events) != 0 {
		t.Errorf("stored events = %d, want 0", total)
	}
}

func TestSecurityEvent_RepeatedVerifiedViolationsSuspend(t *testing.T) {
	h := setupSecurityHarness(t)
	lic := h.createLicense(t, "sec-repeat@example.com", model.StatusActive, nil)
	ident := "kgid:v1:linux:" + fmt.Sprintf("%064x", 6)
	priv := h.activateSigned(t, lic, ident)

	for i := 1; i <= 2; i++ {
		code, resp := h.postReport(h.signedReport(t, lic, ident,
			fmt.Sprintf("evt-repeat-%d", i), model.SecurityEventIntegrityViolation, priv))
		if code != http.StatusOK {
			t.Fatalf("report %d: %d %v", i, code, resp)
		}
		if got := dataOf(t, resp)["license_status"]; got != model.StatusActive {
			t.Errorf("report %d status = %v, want active", i, got)
		}
	}

	code, resp := h.postReport(h.signedReport(t, lic, ident, "evt-repeat-3", model.SecurityEventIntegrityViolation, priv))
	if code != http.StatusOK {
		t.Fatalf("third report: %d %v", code, resp)
	}
	d := dataOf(t, resp)
	if d["action_taken"] != model.PolicyActionSuspend {
		t.Errorf("action = %v, want suspend", d["action_taken"])
	}
	if d["license_status"] != model.StatusSuspended {
		t.Errorf("status = %v, want suspended", d["license_status"])
	}
	fresh, _ := h.store.FindLicenseByID(context.Background(), lic.ID)
	if fresh.Status != model.StatusSuspended {
		t.Errorf("db status = %q, want suspended", fresh.Status)
	}
	if fresh.SuspendedBy != model.SuspendedBySecurityPolicy {
		t.Errorf("suspended_by = %q, want %q", fresh.SuspendedBy, model.SuspendedBySecurityPolicy)
	}

	// The suspension is server-authoritative: verify now refuses.
	code, _ = h.postVerify(map[string]any{
		"license_key": lic.LicenseKey,
		"identifier":  ident,
	})
	if code != http.StatusNotFound {
		t.Errorf("verify after suspension = %d, want 404", code)
	}
}

func TestSecurityEvent_UnverifiedReportsNeverSuspend(t *testing.T) {
	h := setupSecurityHarness(t)
	lic := h.createLicense(t, "sec-legacy@example.com", model.StatusActive, nil)
	ident := "kgid:v1:linux:" + fmt.Sprintf("%064x", 7)
	// Legacy activation: no device key registered.
	code, resp := h.postActivate(map[string]any{
		"license_key": lic.LicenseKey,
		"identifier":  ident,
	})
	if code != http.StatusOK {
		t.Fatalf("activate: %d %v", code, resp)
	}

	// Configure the most aggressive policy possible for this type.
	rules := []*model.SecurityPolicy{{
		EventType: model.SecurityEventIntegrityViolation, Action: model.PolicyActionRevoke,
		Threshold: 2, WindowSeconds: 3600, Enabled: true,
	}}
	if err := h.secSvc.ReplacePolicies(context.Background(), h.product.ID, rules); err != nil {
		t.Fatalf("replace policies: %v", err)
	}

	for i := 1; i <= 3; i++ {
		body := map[string]any{
			"license_key": lic.LicenseKey,
			"identifier":  ident,
			"event_id":    fmt.Sprintf("evt-unverified-%d", i),
			"event_type":  model.SecurityEventIntegrityViolation,
			"occurred_at": time.Now().Unix(),
		}
		code, resp := h.postReport(body)
		if code != http.StatusOK {
			t.Fatalf("report %d: %d %v", i, code, resp)
		}
		d := dataOf(t, resp)
		if d["verified"] != false {
			t.Errorf("verified = %v, want false", d["verified"])
		}
		if d["action_taken"] != "unverified" {
			t.Errorf("action = %v, want unverified", d["action_taken"])
		}
	}
	fresh, _ := h.store.FindLicenseByID(context.Background(), lic.ID)
	if fresh.Status != model.StatusActive {
		t.Errorf("status = %q, want active (unverified reports must not act)", fresh.Status)
	}
	if fresh.UnderReview {
		t.Error("unverified reports must not even flag the licence")
	}
}

func TestSecurityEvent_PolicyValidationRefusesUnsafeRevoke(t *testing.T) {
	h := setupSecurityHarness(t)
	ctx := context.Background()

	bad := []*model.SecurityPolicy{
		{EventType: "*", Action: model.PolicyActionRevoke, Threshold: 5, WindowSeconds: 3600},
		{EventType: model.SecurityEventIntegrityViolation, Action: model.PolicyActionRevoke, Threshold: 1, WindowSeconds: 3600},
		{EventType: "made_up_type", Action: model.PolicyActionFlag, Threshold: 1, WindowSeconds: 3600},
		{EventType: model.SecurityEventIntegrityViolation, Action: "delete_everything", Threshold: 1, WindowSeconds: 3600},
	}
	for i, rule := range bad {
		if err := h.secSvc.ReplacePolicies(ctx, h.product.ID, []*model.SecurityPolicy{rule}); err == nil {
			t.Errorf("rule %d (%+v) was accepted, want refusal", i, rule)
		}
	}

	good := []*model.SecurityPolicy{
		{EventType: model.SecurityEventIntegrityViolation, Action: model.PolicyActionRevoke, Threshold: 3, WindowSeconds: 3600},
	}
	if err := h.secSvc.ReplacePolicies(ctx, h.product.ID, good); err != nil {
		t.Errorf("safe revoke rule refused: %v", err)
	}
}

func TestSecurityEvent_AdminConfirmAndSuspendIsAudited(t *testing.T) {
	h := setupSecurityHarness(t)
	lic := h.createLicense(t, "sec-admin@example.com", model.StatusActive, nil)
	ident := "kgid:v1:linux:" + fmt.Sprintf("%064x", 8)
	priv := h.activateSigned(t, lic, ident)

	body := h.signedReport(t, lic, ident, "evt-admin", model.SecurityEventIntegrityViolation, priv)
	if code, resp := h.postReport(body); code != http.StatusOK {
		t.Fatalf("report: %d %v", code, resp)
	}
	events, _, err := h.store.ListSecurityEvents(context.Background(), storeFilter(lic.ID))
	if err != nil || len(events) != 1 {
		t.Fatalf("list events: %v (%d)", err, len(events))
	}

	updated, err := h.secSvc.AdminAction(context.Background(), events[0].ID, model.PolicyActionSuspend, "admin-1", 0)
	if err != nil {
		t.Fatalf("admin action: %v", err)
	}
	if updated.Status != model.StatusSuspended {
		t.Errorf("status = %q, want suspended", updated.Status)
	}
	ev, _ := h.store.FindSecurityEvent(context.Background(), events[0].ID)
	if ev.Status != model.SecurityEventStatusConfirmed || ev.ReviewedBy != "admin-1" {
		t.Errorf("event review = %q by %q, want confirmed by admin-1", ev.Status, ev.ReviewedBy)
	}
	// Audit writes are asynchronous (the store returns before the
	// insert lands), so give the writer a bounded moment to catch up.
	var found bool
	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		logs, _, err := h.store.ListAuditLogs(context.Background(), "license", lic.ID, "", 0, 50)
		if err != nil {
			t.Fatal(err)
		}
		for _, l := range logs {
			if l.Action == "suspended" && l.ActorID == "admin-1" {
				found = true
			}
		}
		if found {
			break
		}
		time.Sleep(20 * time.Millisecond)
	}
	if !found {
		t.Error("audit logs missing admin suspension")
	}
}
