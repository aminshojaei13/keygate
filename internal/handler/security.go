package handler

import (
	"net/http"

	"github.com/gin-gonic/gin"

	"github.com/tabloy/keygate/internal/model"
	"github.com/tabloy/keygate/internal/service"
	"github.com/tabloy/keygate/internal/store"
	"github.com/tabloy/keygate/pkg/apperr"
	"github.com/tabloy/keygate/pkg/response"
)

// SecurityHandler exposes the client report endpoint and the operator
// surface for reviewing reports and tuning reaction policies.
type SecurityHandler struct {
	svc   *service.SecurityService
	store *store.Store
}

func NewSecurityHandler(svc *service.SecurityService, st *store.Store) *SecurityHandler {
	return &SecurityHandler{svc: svc, store: st}
}

const maxSecurityEventBody = 16 << 10

// Report handles POST /api/v1/license/security-events.
//
// Authentication is the license key plus the device identifier, the
// same pair the activation flow already uses. When the device
// registered a public key, the request must also carry a signature
// over the canonical event string; see pkg/securityevent.
func (h *SecurityHandler) Report(c *gin.Context) {
	c.Request.Body = http.MaxBytesReader(c.Writer, c.Request.Body, maxSecurityEventBody)

	var req struct {
		LicenseKey string            `json:"license_key" binding:"required"`
		Identifier string            `json:"identifier" binding:"required"`
		EventID    string            `json:"event_id" binding:"required"`
		EventType  string            `json:"event_type" binding:"required"`
		Severity   string            `json:"severity"`
		OccurredAt int64             `json:"occurred_at"`
		Details    map[string]string `json:"details"`
		Signature  string            `json:"signature"`
	}
	if err := c.ShouldBindJSON(&req); err != nil {
		response.BadRequest(c, "license_key, identifier, event_id and event_type are required")
		return
	}
	if valErr := apperr.ValidateIdentifier(req.Identifier); valErr != nil {
		writeAppErr(c, valErr)
		return
	}

	res, err := h.svc.Report(c.Request.Context(), service.ReportInput{
		LicenseKey: req.LicenseKey,
		ProductID:  productIDFromContext(c),
		Identifier: req.Identifier,
		EventID:    req.EventID,
		EventType:  req.EventType,
		Severity:   req.Severity,
		OccurredAt: req.OccurredAt,
		Details:    req.Details,
		Signature:  req.Signature,
		IPAddress:  c.ClientIP(),
	})
	if err != nil {
		writeAppErr(c, err)
		return
	}
	response.OK(c, res)
}

// ─── Admin: policies ───

// GetPolicy handles GET /api/v1/admin/products/:id/security-policy.
//
// The response carries the explicit rows and, separately, the built-in
// defaults. A dashboard can therefore show what is actually in force
// without having to duplicate the Go defaults in TypeScript.
func (h *SecurityHandler) GetPolicy(c *gin.Context) {
	productID := c.Param("id")
	if !requireKeyProductScope(c, productID) {
		return
	}
	if _, err := h.store.FindProductByID(c, productID); err != nil {
		response.NotFound(c, "product not found")
		return
	}
	rules, err := h.svc.ListPolicies(c, productID)
	if err != nil {
		response.Internal(c, err)
		return
	}
	if rules == nil {
		rules = []*model.SecurityPolicy{}
	}
	response.OK(c, gin.H{
		"rules":    rules,
		"defaults": model.DefaultSecurityPolicies(),
		"actions": []string{
			model.PolicyActionLog, model.PolicyActionFlag, model.PolicyActionReject,
			model.PolicyActionBlockActivation, model.PolicyActionSuspend, model.PolicyActionRevoke,
		},
		"event_types": model.SecurityEventTypes,
	})
}

// UpdatePolicy handles PUT /api/v1/admin/products/:id/security-policy.
//
// Body: { "rules": [ { "event_type": "integrity_violation",
// "action": "suspend", "threshold": 3, "window_seconds": 86400,
// "suspend_days": 0, "enabled": true } ] }
func (h *SecurityHandler) UpdatePolicy(c *gin.Context) {
	productID := c.Param("id")
	if !requireKeyProductScope(c, productID) {
		return
	}
	if _, err := h.store.FindProductByID(c, productID); err != nil {
		response.NotFound(c, "product not found")
		return
	}
	var req struct {
		Rules []*model.SecurityPolicy `json:"rules" binding:"required"`
	}
	if err := c.ShouldBindJSON(&req); err != nil {
		response.BadRequest(c, "rules is required")
		return
	}
	if err := h.svc.ReplacePolicies(c.Request.Context(), productID, req.Rules); err != nil {
		writeAppErr(c, err)
		return
	}
	h.store.Audit(c, &model.AuditLog{
		Entity: "product", EntityID: productID, Action: "security_policy_updated",
		ActorType: "admin", ActorID: adminID(c),
		Changes: map[string]any{"rule_count": len(req.Rules)},
	})
	rules, err := h.svc.ListPolicies(c, productID)
	if err != nil {
		response.Internal(c, err)
		return
	}
	response.OK(c, gin.H{"rules": rules})
}

// ─── Admin: events ───

// ListEvents handles GET /api/v1/admin/security-events.
func (h *SecurityHandler) ListEvents(c *gin.Context) {
	page := listPage(c)
	filter := store.SecurityEventFilter{
		ProductID: c.Query("product_id"),
		LicenseID: c.Query("license_id"),
		Status:    c.Query("status"),
		EventType: c.Query("event_type"),
		Offset:    page.Offset,
		Limit:     page.Limit,
	}
	if v := c.Query("verified"); v == "true" || v == "false" {
		b := v == "true"
		filter.Verified = &b
	}
	// An API key bound to one product must not read another product's
	// reports, even if it holds the admin scope.
	if pid := keyProductScope(c); pid != "" {
		filter.ProductID = pid
	}
	events, total, err := h.store.ListSecurityEvents(c, filter)
	if err != nil {
		response.Internal(c, err)
		return
	}
	listOK(c, "security_events", events, total, page)
}

// ActOnEvent handles POST /api/v1/admin/security-events/:id/action.
//
// Body: { "action": "suspend", "suspend_days": 0, "confirm_revoke": true }
func (h *SecurityHandler) ActOnEvent(c *gin.Context) {
	ev, err := h.store.FindSecurityEvent(c, c.Param("id"))
	if err != nil {
		response.NotFound(c, "security event not found")
		return
	}
	if !requireKeyProductScope(c, ev.ProductID) {
		return
	}
	var req struct {
		Action        string `json:"action" binding:"required"`
		SuspendDays   int    `json:"suspend_days"`
		ConfirmRevoke bool   `json:"confirm_revoke"`
	}
	if err := c.ShouldBindJSON(&req); err != nil {
		response.BadRequest(c, "action is required")
		return
	}
	if req.Action == model.PolicyActionRevoke && !req.ConfirmRevoke {
		response.Err(c, http.StatusConflict, "REVOKE_NOT_CONFIRMED",
			"revoking a license is irreversible; send confirm_revoke=true after reviewing the event")
		return
	}
	if req.SuspendDays < 0 || req.SuspendDays > 3650 {
		response.BadRequest(c, "suspend_days must be between 0 and 3650")
		return
	}
	lic, err := h.svc.AdminAction(c.Request.Context(), ev.ID, req.Action, adminID(c), req.SuspendDays)
	if err != nil {
		writeAppErr(c, err)
		return
	}
	response.OK(c, gin.H{"license": lic, "status": lic.Status})
}

// DismissEvent handles POST /api/v1/admin/security-events/:id/dismiss.
func (h *SecurityHandler) DismissEvent(c *gin.Context) {
	ev, err := h.store.FindSecurityEvent(c, c.Param("id"))
	if err != nil {
		response.NotFound(c, "security event not found")
		return
	}
	if !requireKeyProductScope(c, ev.ProductID) {
		return
	}
	if err := h.svc.Dismiss(c.Request.Context(), ev.ID, adminID(c)); err != nil {
		writeAppErr(c, err)
		return
	}
	response.OK(c, gin.H{"status": "dismissed"})
}

// ResetSecurityState handles POST /api/v1/admin/licenses/:id/security-reset.
//
// The reversible half of the policy: clear the review marker and/or
// lift an activation block. Suspension is lifted through the existing
// reinstate endpoint, which already knows how to restore a trial or a
// paid status.
func (h *SecurityHandler) ResetSecurityState(c *gin.Context) {
	id := c.Param("id")
	if !checkLicenseProductScope(c, h.store, id) {
		return
	}
	var req struct {
		ClearReview          bool `json:"clear_review"`
		ClearActivationBlock bool `json:"clear_activation_block"`
	}
	if err := c.ShouldBindJSON(&req); err != nil {
		response.BadRequest(c, "request body is required")
		return
	}
	if req.ClearReview {
		if _, err := h.store.ClearLicenseReview(c, id); err != nil {
			response.Internal(c, err)
			return
		}
	}
	if req.ClearActivationBlock {
		if _, err := h.store.SetLicenseActivationBlocked(c, id, false); err != nil {
			response.Internal(c, err)
			return
		}
	}
	h.store.Audit(c, &model.AuditLog{
		Entity: "license", EntityID: id, Action: "security_state_reset",
		ActorType: "admin", ActorID: adminID(c),
		Changes: map[string]any{
			"clear_review":           req.ClearReview,
			"clear_activation_block": req.ClearActivationBlock,
		},
	})
	response.OK(c, gin.H{"status": "updated"})
}

// checkLicenseProductScope is the security handler's copy of the
// license-id → product → API-key scope check. It exists as a free
// function because AdminHandler's method is bound to that type.
func checkLicenseProductScope(c *gin.Context, st *store.Store, licenseID string) bool {
	pid, err := st.GetLicenseProductID(c, licenseID)
	if err != nil {
		response.NotFound(c, "license not found")
		return false
	}
	return requireKeyProductScope(c, pid)
}

// keyProductScope returns the product an API key is bound to, or "".
func keyProductScope(c *gin.Context) string {
	v, ok := c.Get("api_key")
	if !ok {
		return ""
	}
	ak, ok := v.(*model.APIKey)
	if !ok || ak == nil {
		return ""
	}
	return ak.ProductID
}

func productIDFromContext(c *gin.Context) string {
	v, _ := c.Get("product_id")
	return str(v)
}
