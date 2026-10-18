package handler

import (
	"bytes"
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"encoding/json"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"sync"
	"testing"
	"time"

	"github.com/gin-gonic/gin"
	"github.com/tabloy/keygate/internal/model"
	"github.com/tabloy/keygate/internal/service"
	"github.com/tabloy/keygate/internal/store"
)

type testHarness struct {
	store      *store.Store
	licSvc     *service.LicenseService
	licHandler *LicenseHandler
	adminH     *AdminHandler
	portalH    *PortalActivationsHandler
	signingKey ed25519.PrivateKey
	product    *model.Product
	plan       *model.Plan
}

func setupActivationHarness(t *testing.T, maxActivations int) *testHarness {
	t.Helper()
	s := scratchStore(t)
	gin.SetMode(gin.TestMode)

	_, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("generate key: %v", err)
	}

	logger := slog.New(slog.NewTextHandler(io.Discard, nil))
	webhookSvc := service.NewWebhookService(s, logger, 2*time.Second, 1, false)
	licSvc := service.NewLicenseService(s, priv, logger, nil, webhookSvc)
	licHandler := NewLicenseHandler(licSvc)
	adminH := &AdminHandler{Store: s, Webhook: webhookSvc}
	portalH := NewPortalActivationsHandler(s, webhookSvc)

	ctx := context.Background()
	suffix := fmt.Sprintf("%d", time.Now().UnixNano())
	product := &model.Product{
		Name: "Desktop App " + suffix,
		Slug: "desktop-app-" + suffix,
		Type: "desktop",
	}
	if err := s.CreateProduct(ctx, product); err != nil {
		t.Fatalf("create product: %v", err)
	}

	plan := &model.Plan{
		ProductID:      product.ID,
		Name:           "Pro Plan",
		Slug:           "pro-" + suffix,
		LicenseType:    "perpetual",
		LicenseModel:   "standard",
		MaxActivations: maxActivations,
	}
	if err := s.CreatePlan(ctx, plan); err != nil {
		t.Fatalf("create plan: %v", err)
	}

	return &testHarness{
		store:      s,
		licSvc:     licSvc,
		licHandler: licHandler,
		adminH:     adminH,
		portalH:    portalH,
		signingKey: priv,
		product:    product,
		plan:       plan,
	}
}

func (h *testHarness) createLicense(t *testing.T, email, status string, validUntil *time.Time) *model.License {
	t.Helper()
	ctx := context.Background()
	suffix := fmt.Sprintf("%d", time.Now().UnixNano())
	lic := &model.License{
		ProductID:  h.product.ID,
		PlanID:     h.plan.ID,
		Email:      email,
		LicenseKey: fmt.Sprintf("KG-TEST-%s", suffix),
		Status:     status,
		ValidUntil: validUntil,
	}
	if err := h.store.CreateLicense(ctx, lic); err != nil {
		t.Fatalf("create license: %v", err)
	}
	return lic
}

func (h *testHarness) postActivate(body map[string]any) (int, map[string]any) {
	b, _ := json.Marshal(body)
	w := httptest.NewRecorder()
	c, _ := gin.CreateTestContext(w)
	c.Request = httptest.NewRequest(http.MethodPost, "/api/v1/license/activate", bytes.NewReader(b))
	c.Request.Header.Set("Content-Type", "application/json")
	h.licHandler.Activate(c)

	var resp map[string]any
	_ = json.Unmarshal(w.Body.Bytes(), &resp)
	return w.Code, resp
}

func TestLicenseActivation_NormalAndMetadata(t *testing.T) {
	h := setupActivationHarness(t, 2)
	lic := h.createLicense(t, "user@example.com", model.StatusActive, nil)

	identUUID := "a0b1c2d3-e4f5-4a6b-8c9d-0e1f2a3b4c5d"
	code, resp := h.postActivate(map[string]any{
		"license_key":     lic.LicenseKey,
		"identifier":      identUUID,
		"identifier_type": "device",
		"label":           "My Macbook",
	})
	if code != http.StatusOK {
		t.Fatalf("status = %d, body = %v", code, resp)
	}
	data, ok := resp["data"].(map[string]any)
	if !ok {
		t.Fatalf("missing data envelope in response: %v", resp)
	}
	if data["status"] != "activated" {
		t.Errorf("status = %v, want activated", data["status"])
	}
	if data["token"] == "" {
		t.Error("expected non-empty token")
	}

	// Verify database record
	ctx := context.Background()
	act, err := h.store.FindActivation(ctx, lic.ID, identUUID)
	if err != nil {
		t.Fatalf("find activation: %v", err)
	}
	if act.Scheme != model.ActivationSchemeUUID {
		t.Errorf("scheme = %q, want %q", act.Scheme, model.ActivationSchemeUUID)
	}
	if act.Status != model.ActivationActive {
		t.Errorf("status = %q, want active", act.Status)
	}
	if act.Label != "My Macbook" {
		t.Errorf("label = %q, want 'My Macbook'", act.Label)
	}
	if act.CreatedAt.IsZero() {
		t.Error("created_at must not be zero")
	}
	if act.LastVerified.IsZero() {
		t.Error("last_verified must not be zero")
	}
}

func TestLicenseActivation_DuplicateRequests(t *testing.T) {
	h := setupActivationHarness(t, 2)
	lic := h.createLicense(t, "dup@example.com", model.StatusActive, nil)
	ident := "machine-dup-test"

	// First request
	code1, resp1 := h.postActivate(map[string]any{
		"license_key": lic.LicenseKey,
		"identifier":  ident,
	})
	if code1 != http.StatusOK {
		t.Fatalf("code1 = %d", code1)
	}
	data1 := resp1["data"].(map[string]any)
	if data1["status"] != "activated" {
		t.Fatalf("status1 = %v, want activated", data1["status"])
	}

	// Second request from the same machine
	code2, resp2 := h.postActivate(map[string]any{
		"license_key": lic.LicenseKey,
		"identifier":  ident,
	})
	if code2 != http.StatusOK {
		t.Fatalf("code2 = %d, want 200", code2)
	}
	data2 := resp2["data"].(map[string]any)
	if data2["status"] != "already_activated" {
		t.Errorf("status2 = %v, want already_activated", data2["status"])
	}
	if data2["token"] == "" {
		t.Error("expected valid token on repeat request")
	}

	// Must consume only 1 slot
	count, err := h.store.CountActivations(context.Background(), lic.ID)
	if err != nil {
		t.Fatalf("count: %v", err)
	}
	if count != 1 {
		t.Errorf("activations count = %d, want 1", count)
	}
}

func TestLicenseActivation_ActivationLimits(t *testing.T) {
	h := setupActivationHarness(t, 2)
	lic := h.createLicense(t, "cap@example.com", model.StatusActive, nil)

	// Activate slot 1
	code, _ := h.postActivate(map[string]any{
		"license_key": lic.LicenseKey,
		"identifier":  "slot-1",
	})
	if code != http.StatusOK {
		t.Fatalf("slot-1 activate code = %d", code)
	}

	// Activate slot 2
	code, _ = h.postActivate(map[string]any{
		"license_key": lic.LicenseKey,
		"identifier":  "slot-2",
	})
	if code != http.StatusOK {
		t.Fatalf("slot-2 activate code = %d", code)
	}

	// Attempt slot 3 -> must fail with 409 ACTIVATION_LIMIT
	code, resp := h.postActivate(map[string]any{
		"license_key": lic.LicenseKey,
		"identifier":  "slot-3",
	})
	if code != http.StatusConflict {
		t.Fatalf("slot-3 activate code = %d, want 409", code)
	}
	errObj, ok := resp["error"].(map[string]any)
	if !ok {
		t.Fatalf("missing error envelope: %v", resp)
	}
	if errObj["code"] != "ACTIVATION_LIMIT" {
		t.Errorf("error code = %v, want ACTIVATION_LIMIT", errObj["code"])
	}
	details, ok := errObj["details"].(map[string]any)
	if !ok {
		t.Fatalf("missing details in error: %v", errObj)
	}
	if details["max"] != float64(2) {
		t.Errorf("details.max = %v, want 2", details["max"])
	}
	if details["current"] != float64(2) {
		t.Errorf("details.current = %v, want 2", details["current"])
	}

	// Audit failure must be recorded (async insert)
	ctx := context.Background()
	var auditCount int
	for deadline := time.Now().Add(3 * time.Second); time.Now().Before(deadline); time.Sleep(50 * time.Millisecond) {
		err := h.store.DB.NewRaw(`
			SELECT count(*) FROM audit_logs
			WHERE entity_id = ? AND action = 'activation_failed'
		`, lic.ID).Scan(ctx, &auditCount)
		if err == nil && auditCount >= 1 {
			break
		}
	}
	if auditCount < 1 {
		t.Errorf("audit activation_failed count = %d, want >= 1", auditCount)
	}
}

func TestLicenseActivation_InvalidAndRevokedLicenses(t *testing.T) {
	h := setupActivationHarness(t, 2)

	// Non-existent license
	code, resp := h.postActivate(map[string]any{
		"license_key": "KG-NONEXISTENT-KEY",
		"identifier":  "dev-1",
	})
	if code != http.StatusNotFound {
		t.Errorf("nonexistent key code = %d, want 404", code)
	}

	// Revoked license
	revLic := h.createLicense(t, "revoked@example.com", model.StatusRevoked, nil)
	code, resp = h.postActivate(map[string]any{
		"license_key": revLic.LicenseKey,
		"identifier":  "dev-1",
	})
	if code != http.StatusForbidden {
		t.Errorf("revoked key code = %d, want 403", code)
	}
	if resp["error"].(map[string]any)["code"] != "LICENSE_REVOKED" {
		t.Errorf("error code = %v, want LICENSE_REVOKED", resp["error"])
	}

	// Expired license
	past := time.Now().Add(-24 * time.Hour)
	expLic := h.createLicense(t, "expired@example.com", model.StatusActive, &past)
	code, resp = h.postActivate(map[string]any{
		"license_key": expLic.LicenseKey,
		"identifier":  "dev-1",
	})
	if code != http.StatusForbidden {
		t.Errorf("expired key code = %d, want 403", code)
	}
	if resp["error"].(map[string]any)["code"] != "LICENSE_EXPIRED" {
		t.Errorf("error code = %v, want LICENSE_EXPIRED", resp["error"])
	}

	// Suspended license
	suspLic := h.createLicense(t, "suspended@example.com", model.StatusSuspended, nil)
	code, resp = h.postActivate(map[string]any{
		"license_key": suspLic.LicenseKey,
		"identifier":  "dev-1",
	})
	if code != http.StatusForbidden {
		t.Errorf("suspended key code = %d, want 403", code)
	}
	if resp["error"].(map[string]any)["code"] != "LICENSE_SUSPENDED" {
		t.Errorf("error code = %v, want LICENSE_SUSPENDED", resp["error"])
	}
}

func TestLicenseActivation_ConcurrentActivations(t *testing.T) {
	const max = 2
	const contenders = 10

	h := setupActivationHarness(t, max)
	lic := h.createLicense(t, "concurrent@example.com", model.StatusActive, nil)

	var wg sync.WaitGroup
	statusCodes := make([]int, contenders)
	for i := range contenders {
		wg.Add(1)
		go func(idx int) {
			defer wg.Done()
			code, _ := h.postActivate(map[string]any{
				"license_key": lic.LicenseKey,
				"identifier":  fmt.Sprintf("race-machine-%02d", idx),
			})
			statusCodes[idx] = code
		}(i)
	}
	wg.Wait()

	successCount := 0
	conflictCount := 0
	for _, code := range statusCodes {
		switch code {
		case http.StatusOK:
			successCount++
		case http.StatusConflict:
			conflictCount++
		default:
			t.Errorf("unexpected status code: %d", code)
		}
	}

	if successCount != max {
		t.Errorf("success count = %d, want exactly %d", successCount, max)
	}
	if conflictCount != contenders-max {
		t.Errorf("conflict count = %d, want %d", conflictCount, contenders-max)
	}

	activeCount, err := h.store.CountActivations(context.Background(), lic.ID)
	if err != nil {
		t.Fatalf("count: %v", err)
	}
	if activeCount != max {
		t.Errorf("active in DB = %d, want %d", activeCount, max)
	}
}

func TestLicenseTransfer_AdminWorkflow(t *testing.T) {
	h := setupActivationHarness(t, 2)
	lic := h.createLicense(t, "source@example.com", model.StatusActive, nil)

	// Activate 2 machines
	h.postActivate(map[string]any{"license_key": lic.LicenseKey, "identifier": "dev-1"})
	h.postActivate(map[string]any{"license_key": lic.LicenseKey, "identifier": "dev-2"})

	transfer := func(targetLicID string, body map[string]any, role, productScope string) (int, map[string]any) {
		b, _ := json.Marshal(body)
		w := httptest.NewRecorder()
		c, _ := gin.CreateTestContext(w)
		c.Params = gin.Params{{Key: "id", Value: targetLicID}}
		c.Request = httptest.NewRequest(http.MethodPost, "/admin/licenses/"+targetLicID+"/transfer", bytes.NewReader(b))
		c.Request.Header.Set("Content-Type", "application/json")
		if role != "" {
			c.Set("role", role)
		}
		if productScope != "" {
			c.Set("api_key", &model.APIKey{ProductID: productScope})
		}
		h.adminH.TransferLicense(c)
		var resp map[string]any
		_ = json.Unmarshal(w.Body.Bytes(), &resp)
		return w.Code, resp
	}

	// 1. Unauthorized / product scope mismatch
	code, resp := transfer(lic.ID, map[string]any{"new_email": "target@example.com"}, "admin", "different-product-id")
	if code != http.StatusForbidden {
		t.Errorf("product mismatch code = %d, want 403", code)
	}
	if resp["error"].(map[string]any)["code"] != "PRODUCT_SCOPE_MISMATCH" {
		t.Errorf("error code = %v, want PRODUCT_SCOPE_MISMATCH", resp["error"])
	}

	// 2. Bad email format
	code, resp = transfer(lic.ID, map[string]any{"new_email": "not-an-email"}, "admin", h.product.ID)
	if code != http.StatusBadRequest {
		t.Errorf("bad email code = %d, want 400", code)
	}
	if resp["error"].(map[string]any)["code"] != "INVALID_INPUT" {
		t.Errorf("error code = %v, want INVALID_INPUT", resp["error"])
	}

	// 3. Identical email
	code, resp = transfer(lic.ID, map[string]any{"new_email": lic.Email}, "admin", h.product.ID)
	if code != http.StatusConflict {
		t.Errorf("identical email code = %d, want 409", code)
	}
	if resp["error"].(map[string]any)["code"] != "IDENTICAL_EMAIL" {
		t.Errorf("error code = %v, want IDENTICAL_EMAIL", resp["error"])
	}

	// 4. Successful transfer with release_activations: true
	newEmail := "target@example.com"
	code, resp = transfer(lic.ID, map[string]any{
		"new_email":           newEmail,
		"release_activations": true,
		"reason":              "support ticket #42",
	}, "admin", h.product.ID)
	if code != http.StatusOK {
		t.Fatalf("transfer code = %d, want 200: %v", code, resp)
	}
	data := resp["data"].(map[string]any)
	if data["status"] != "transferred" {
		t.Errorf("status = %v, want transferred", data["status"])
	}
	if data["email"] != newEmail {
		t.Errorf("email = %v, want %q", data["email"], newEmail)
	}
	if data["activations_released"] != float64(2) {
		t.Errorf("activations_released = %v, want 2", data["activations_released"])
	}

	// Verify license record updated
	ctx := context.Background()
	updatedLic, err := h.store.FindLicenseByID(ctx, lic.ID)
	if err != nil {
		t.Fatalf("find license: %v", err)
	}
	if updatedLic.Email != newEmail {
		t.Errorf("license email = %q, want %q", updatedLic.Email, newEmail)
	}

	// Verify active count is 0
	activeCount, err := h.store.CountActivations(ctx, lic.ID)
	if err != nil {
		t.Fatalf("count: %v", err)
	}
	if activeCount != 0 {
		t.Errorf("active count = %d, want 0", activeCount)
	}

	// Verify audit log has no secrets
	var auditAction string
	var auditChanges map[string]any
	err = h.store.DB.NewRaw(`
		SELECT action, changes FROM audit_logs
		WHERE entity_id = ? AND action = 'transferred'
		ORDER BY id DESC LIMIT 1
	`, lic.ID).Scan(ctx, &auditAction, &auditChanges)
	if err != nil {
		t.Fatalf("query audit log: %v", err)
	}
	if auditAction != "transferred" {
		t.Errorf("audit action = %q, want transferred", auditAction)
	}
	if auditChanges["from_email"] != lic.Email || auditChanges["to_email"] != newEmail {
		t.Errorf("audit changes unexpected: %v", auditChanges)
	}
	if _, hasKey := auditChanges["license_key"]; hasKey {
		t.Error("sensitive license_key must not be in audit changes")
	}
}

func TestAdminActivation_DeactivateAndReactivate(t *testing.T) {
	h := setupActivationHarness(t, 1)
	lic := h.createLicense(t, "adminact@example.com", model.StatusActive, nil)

	// Activate device 1
	code, _ := h.postActivate(map[string]any{"license_key": lic.LicenseKey, "identifier": "dev-1"})
	if code != http.StatusOK {
		t.Fatalf("activate dev-1 code = %d", code)
	}

	ctx := context.Background()
	act1, err := h.store.FindActivation(ctx, lic.ID, "dev-1")
	if err != nil {
		t.Fatalf("find activation: %v", err)
	}

	// 1. Admin deactivates device 1
	w := httptest.NewRecorder()
	c, _ := gin.CreateTestContext(w)
	c.Params = gin.Params{{Key: "id", Value: act1.ID}}
	c.Request = httptest.NewRequest(http.MethodPost, "/admin/activations/"+act1.ID+"/deactivate", nil)
	c.Set("role", "admin")
	c.Set("api_key", &model.APIKey{ProductID: h.product.ID})
	h.adminH.DeactivateActivation(c)
	if w.Code != http.StatusOK {
		t.Fatalf("deactivate code = %d, want 200", w.Code)
	}

	// Slot is now free
	count, _ := h.store.CountActivations(ctx, lic.ID)
	if count != 0 {
		t.Errorf("count after deactivation = %d, want 0", count)
	}

	// Activate device 2 into the freed slot
	code, _ = h.postActivate(map[string]any{"license_key": lic.LicenseKey, "identifier": "dev-2"})
	if code != http.StatusOK {
		t.Fatalf("activate dev-2 code = %d", code)
	}

	// 2. Admin attempts to reactivate device 1 -> must fail with 409 ACTIVATION_LIMIT
	w = httptest.NewRecorder()
	c, _ = gin.CreateTestContext(w)
	c.Params = gin.Params{{Key: "id", Value: act1.ID}}
	c.Request = httptest.NewRequest(http.MethodPost, "/admin/activations/"+act1.ID+"/reactivate", nil)
	c.Set("role", "admin")
	c.Set("api_key", &model.APIKey{ProductID: h.product.ID})
	h.adminH.ReactivateActivation(c)
	if w.Code != http.StatusConflict {
		t.Fatalf("reactivate code = %d, want 409", w.Code)
	}
	var resp map[string]any
	_ = json.Unmarshal(w.Body.Bytes(), &resp)
	if resp["error"].(map[string]any)["code"] != "ACTIVATION_LIMIT" {
		t.Errorf("error code = %v, want ACTIVATION_LIMIT", resp["error"])
	}
}

func TestLicenseActivation_VersionedIdentifierKGID(t *testing.T) {
	h := setupActivationHarness(t, 2)
	lic := h.createLicense(t, "kgid@example.com", model.StatusActive, nil)

	identKGID := "kgid:v1:darwin:5581b5a7b290e096fdea9b19916561e13d15fc983a9b57c53a9e5ad91f29f4b7"
	code, resp := h.postActivate(map[string]any{
		"license_key": lic.LicenseKey,
		"identifier":  identKGID,
		"label":       "Darwin M3 Workstation",
	})
	if code != http.StatusOK {
		t.Fatalf("kgid activate code = %d, resp = %v", code, resp)
	}

	data := resp["data"].(map[string]any)
	if data["status"] != "activated" {
		t.Errorf("status = %v, want activated", data["status"])
	}

	ctx := context.Background()
	act, err := h.store.FindActivation(ctx, lic.ID, identKGID)
	if err != nil {
		t.Fatalf("find activation: %v", err)
	}
	if act.Scheme != model.ActivationSchemeKGIDv1 {
		t.Errorf("scheme = %q, want %q", act.Scheme, model.ActivationSchemeKGIDv1)
	}
}

func TestLicenseActivation_InvalidIdentifierFormats(t *testing.T) {
	h := setupActivationHarness(t, 2)
	lic := h.createLicense(t, "invalid_fmt@example.com", model.StatusActive, nil)

	invalids := []string{
		"   ",
		"device with spaces",
		"kgid:v1:darwin:tooshort",
		"kgid:v999:unknown:5581b5a7b290e096fdea9b19916561e13d15fc983a9b57c53a9e5ad91f29f4b7",
		"sha256:not-sixty-four-hex",
		"uuid:malformed-uuid",
		strings.Repeat("a", 257),
	}

	for _, inv := range invalids {
		code, resp := h.postActivate(map[string]any{
			"license_key": lic.LicenseKey,
			"identifier":  inv,
		})
		if code != http.StatusBadRequest {
			t.Errorf("expected 400 for identifier %q, got %d (resp: %v)", inv, code, resp)
		}
	}
}

func TestLicenseActivation_VersionMigrationWithPreviousIdentifier(t *testing.T) {
	// Plan with max=1 activation to test that migration does not exhaust slots
	h := setupActivationHarness(t, 1)
	lic := h.createLicense(t, "migration@example.com", model.StatusActive, nil)

	oldUUID := "550e8400-e29b-41d4-a716-446655440000"
	newKGID := "kgid:v1:windows:5581b5a7b290e096fdea9b19916561e13d15fc983a9b57c53a9e5ad91f29f4b7"

	// 1. Initial activation with old UUID
	code, resp := h.postActivate(map[string]any{
		"license_key": lic.LicenseKey,
		"identifier":  oldUUID,
		"label":       "Old Windows PC",
	})
	if code != http.StatusOK {
		t.Fatalf("initial activate code = %d", code)
	}

	// 2. Client upgrades to kgid-v1 and sends previous_identifier
	// Even though max=1 is currently full, migration must succeed in-place!
	code, resp = h.postActivate(map[string]any{
		"license_key":         lic.LicenseKey,
		"identifier":          newKGID,
		"previous_identifier": oldUUID,
		"label":               "Upgraded Windows PC",
	})
	if code != http.StatusOK {
		t.Fatalf("migration activate code = %d, want 200: %v", code, resp)
	}
	data := resp["data"].(map[string]any)
	if data["status"] != "activated" {
		t.Errorf("status = %v, want activated", data["status"])
	}

	// Active slot count in database must remain exactly 1
	ctx := context.Background()
	count, err := h.store.CountActivations(ctx, lic.ID)
	if err != nil {
		t.Fatalf("count: %v", err)
	}
	if count != 1 {
		t.Errorf("active count = %d, want 1", count)
	}

	// The old UUID must no longer be found as active
	_, err = h.store.FindActivation(ctx, lic.ID, oldUUID)
	if err == nil {
		t.Error("old identifier should no longer be active")
	}

	// The new KGID must be active with scheme kgid-v1
	newAct, err := h.store.FindActivation(ctx, lic.ID, newKGID)
	if err != nil {
		t.Fatalf("find new activation: %v", err)
	}
	if newAct.Scheme != model.ActivationSchemeKGIDv1 {
		t.Errorf("new scheme = %q, want %q", newAct.Scheme, model.ActivationSchemeKGIDv1)
	}
}

