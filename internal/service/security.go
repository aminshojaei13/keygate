package service

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"log/slog"
	"net/http"
	"strings"
	"time"

	"github.com/tabloy/keygate/internal/model"
	"github.com/tabloy/keygate/internal/store"
	"github.com/tabloy/keygate/pkg/apperr"
	"github.com/tabloy/keygate/pkg/securityevent"
)

// SecurityService accepts client tamper reports and applies the
// product's reaction policy.
//
// Trust model, in one sentence: a report is a signal worth recording,
// never proof on its own. The service therefore separates three
// questions it used to be tempting to collapse into one —
//
//  1. Is this report attached to a real, active device activation?
//     (identity)
//  2. Did the report carry an Ed25519 signature from the key that
//     device registered at activation? (evidence)
//  3. Does the product's policy, with its threshold and window, call
//     for a state change? (decision)
//
// Only answers 1+2+3 together can suspend or revoke. An unverified
// report is stored for an operator to look at and changes nothing.
type SecurityService struct {
	store   *store.Store
	logger  *slog.Logger
	webhook *WebhookService

	// now is injectable so tests can drive windows without sleeping.
	now func() time.Time
	// maxPerHourPerLicense bounds write volume from one licence. The
	// HTTP layer also rate-limits by IP; this one survives NAT and
	// catches a single compromised client.
	maxPerHourPerLicense int
}

func NewSecurityService(s *store.Store, logger *slog.Logger, webhook *WebhookService) *SecurityService {
	if logger == nil {
		logger = slog.Default()
	}
	return &SecurityService{
		store:                s,
		logger:               logger,
		webhook:              webhook,
		now:                  time.Now,
		maxPerHourPerLicense: 200,
	}
}

// ReportInput is one client security report. LicenseKey and
// Signature are credentials; they are never written to the event row
// or the audit trail.
type ReportInput struct {
	LicenseKey string
	ProductID  string
	Identifier string
	EventID    string
	EventType  string
	Severity   string
	OccurredAt int64
	Details    map[string]string
	Signature  string
	IPAddress  string
}

// ReportResult is the narrow answer the SDK needs. It deliberately
// does not disclose which file failed, who else uses the licence, or
// the internal policy id beyond the action name.
type ReportResult struct {
	EventID       string `json:"event_id"`
	Accepted      bool   `json:"accepted"`
	Duplicate     bool   `json:"duplicate"`
	Verified      bool   `json:"verified"`
	ActionTaken   string `json:"action_taken,omitempty"`
	LicenseStatus string `json:"license_status"`
	UnderReview   bool   `json:"under_review"`
	// IsAllowed lets a `reject` policy stop the current operation
	// without changing the licence for other devices. It is true for
	// every other action.
	IsAllowed bool `json:"is_allowed"`
	// RetryAfter is set on the flood guard, in seconds.
	RetryAfter int `json:"retry_after,omitempty"`
}

const (
	securityEventClockSkewPast   = 7 * 24 * time.Hour
	securityEventClockSkewFuture = 10 * time.Minute
)

// Report validates, records and (when the evidence and policy allow)
// acts on a client security report.
func (s *SecurityService) Report(ctx context.Context, in ReportInput) (*ReportResult, error) {
	now := s.now()

	if err := securityevent.ValidateEventID(in.EventID); err != nil {
		return nil, apperr.New(http.StatusBadRequest, "INVALID_EVENT_ID", err.Error())
	}
	if !model.IsSecurityEventType(in.EventType) {
		return nil, apperr.New(http.StatusBadRequest, "UNSUPPORTED_EVENT_TYPE",
			"event_type is not a documented security event type")
	}
	if len(in.Identifier) > securityevent.MaxDeviceIDLength {
		return nil, apperr.New(http.StatusBadRequest, "INVALID_DEVICE_ID", "identifier is too long")
	}
	if in.OccurredAt <= 0 {
		in.OccurredAt = now.Unix()
	}
	occurred := time.Unix(in.OccurredAt, 0).UTC()
	if occurred.After(now.Add(securityEventClockSkewFuture)) ||
		occurred.Before(now.Add(-securityEventClockSkewPast)) {
		return nil, apperr.WithDetails(
			apperr.New(http.StatusBadRequest, "INVALID_EVENT_TIME",
				"occurred_at is outside the accepted clock-skew window"),
			map[string]any{"max_future_seconds": int(securityEventClockSkewFuture.Seconds()),
				"max_past_seconds": int(securityEventClockSkewPast.Seconds())},
		)
	}

	lic, err := s.store.FindLicenseByKey(ctx, in.LicenseKey)
	if err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, licenseNotFound()
		}
		return nil, apperr.Internal(err)
	}
	if in.ProductID != "" && lic.ProductID != in.ProductID {
		return nil, licenseNotFound()
	}
	// Security reporting is part of the activation/device flow, so it
	// follows the same product-capability gate as verify. SaaS products
	// have no per-device identity to report on.
	if err := requireProductCapability(lic.Product, model.CapActivations, "security event reporting"); err != nil {
		return nil, err
	}

	act, err := s.store.FindActivation(ctx, lic.ID, in.Identifier)
	if err != nil {
		// Same generic answer as verify: do not turn this endpoint into
		// an oracle for which identifiers belong to a licence.
		return nil, licenseNotFound()
	}

	if n, cerr := s.store.CountAllSecurityEventsSince(ctx, lic.ID, now.Add(-time.Hour)); cerr == nil && n >= s.maxPerHourPerLicense {
		return nil, apperr.WithDetails(
			apperr.New(http.StatusTooManyRequests, "SECURITY_EVENT_RATE_LIMIT",
				"too many security events for this license"),
			map[string]any{"retry_after": 3600},
		)
	}

	verified := false
	if act.DevicePublicKey != "" {
		if strings.TrimSpace(in.Signature) == "" {
			return nil, apperr.WithDetails(
				apperr.New(http.StatusUnauthorized, "DEVICE_SIGNATURE_REQUIRED",
					"this device registered a report-signing key; sign the report"),
				map[string]any{"algorithm": "ed25519"},
			)
		}
		if len(in.Signature) > securityevent.MaxSignatureLength {
			return nil, apperr.New(http.StatusUnauthorized, "INVALID_DEVICE_SIGNATURE",
				"device signature is malformed")
		}
		pub, perr := securityevent.DecodePublicKey(act.DevicePublicKey)
		if perr != nil {
			s.logger.Error("activation has an undecodable device key", "activation_id", act.ID, "error", perr)
			return nil, apperr.Internal(perr)
		}
		sig, serr := securityevent.DecodeSignature(in.Signature)
		if serr != nil {
			return nil, apperr.New(http.StatusUnauthorized, "INVALID_DEVICE_SIGNATURE",
				"device signature is malformed")
		}
		canonical := securityevent.CanonicalBytes(in.EventID, lic.ID, in.Identifier, in.EventType, in.OccurredAt)
		if verr := securityevent.Verify(pub, sig, canonical); verr != nil {
			return nil, apperr.New(http.StatusUnauthorized, "INVALID_DEVICE_SIGNATURE",
				"device signature does not verify")
		}
		verified = true
	}

	ev := &model.SecurityEvent{
		ProductID:  lic.ProductID,
		LicenseID:  lic.ID,
		EventID:    in.EventID,
		EventType:  in.EventType,
		Severity:   model.ValidSecuritySeverity(in.Severity),
		DeviceID:   in.Identifier,
		Verified:   verified,
		OccurredAt: occurred,
		ReceivedAt: now,
		Source:     "sdk",
		IPAddress:  in.IPAddress,
		Details:    securityevent.SanitizeDetails(in.Details),
		Status:     model.SecurityEventStatusReceived,
	}
	duplicate, err := s.store.CreateSecurityEvent(ctx, ev)
	if err != nil {
		return nil, apperr.Internal(err)
	}
	if duplicate {
		// The first report already drove the decision. Re-applying a
		// policy on a retry is how a duplicate turns into two
		// suspensions (or, worse, a revoke after a suspend).
		return &ReportResult{
			EventID:       in.EventID,
			Accepted:      true,
			Duplicate:     true,
			Verified:      verified,
			LicenseStatus: lic.Status,
			UnderReview:   lic.UnderReview,
			IsAllowed:     true,
		}, nil
	}

	action, ruleID, allowed := s.evaluateAndApply(ctx, lic, ev, now)

	res := &ReportResult{
		EventID:       ev.EventID,
		Accepted:      true,
		Duplicate:     false,
		Verified:      verified,
		ActionTaken:   action,
		LicenseStatus: lic.Status,
		UnderReview:   lic.UnderReview,
		IsAllowed:     allowed,
	}

	// Re-read the lifecycle state after a possible suspension/revoke so
	// the SDK gets the authoritative answer from the same call.
	if fresh, ferr := s.store.FindLicenseByID(ctx, lic.ID); ferr == nil {
		res.LicenseStatus = fresh.Status
		res.UnderReview = fresh.UnderReview
	}

	s.store.Audit(ctx, &model.AuditLog{
		Entity: "license", EntityID: lic.ID,
		Action:    "security_event_reported",
		ActorType: "sdk", IPAddress: in.IPAddress,
		Changes: map[string]any{
			"security_event_id": ev.ID,
			"event_id":          ev.EventID,
			"event_type":        ev.EventType,
			"verified":          ev.Verified,
			"policy_action":     action,
			"policy_rule_id":    ruleID,
		},
	})

	if s.webhook != nil {
		s.webhook.Dispatch(ctx, lic.ProductID, "license.security_event", map[string]any{
			"license_id": lic.ID,
			"event_id":   ev.EventID,
			"event_type": ev.EventType,
			"severity":   ev.Severity,
			"verified":   ev.Verified,
			"action":     action,
		})
	}

	return res, nil
}

// evaluateAndApply resolves the rule, counts verified evidence in the
// window and applies at most one state change. It returns the action
// names reported to the client and whether the current operation may
// continue.
func (s *SecurityService) evaluateAndApply(ctx context.Context, lic *model.License, ev *model.SecurityEvent, now time.Time) (action, ruleID string, allowed bool) {
	allowed = true
	rule := s.resolvePolicy(ctx, lic.ProductID, ev.EventType)

	// An unsigned report is recorded, never acted on. This is the
	// main defence against a forged report suspending someone else's
	// licence: the attacker would need the private key held on the
	// victim's device, not merely a leaked license key.
	if !ev.Verified {
		return "unverified", rule.ID, true
	}
	if rule.Action == model.PolicyActionLog {
		return model.PolicyActionLog, rule.ID, true
	}

	since := now.Add(-time.Duration(rule.WindowSeconds) * time.Second)
	count, err := s.store.CountSecurityEventsSince(ctx, lic.ID, ev.EventType, since, true)
	if err != nil {
		s.logger.Error("count security events", "license_id", lic.ID, "error", err)
		return "log", rule.ID, true
	}

	// Below the threshold the proportionate response is to surface the
	// licence for a human: no access change, no notification to the
	// customer, just a queue.
	if count < rule.Threshold {
		s.flag(ctx, lic, ev, "threshold not reached")
		return model.PolicyActionFlag, rule.ID, true
	}

	if rule.Action == model.PolicyActionRevoke && rule.Threshold < 2 {
		// Defence in depth: policy validation already refuses this,
		// but a row written directly into the database must not turn a
		// single report into a permanent revocation.
		s.logger.Error("refusing a revoke rule with an unsafe threshold",
			"license_id", lic.ID, "threshold", rule.Threshold)
		s.flag(ctx, lic, ev, "unsafe revoke rule downgraded")
		return model.PolicyActionFlag, rule.ID, true
	}

	switch rule.Action {
	case model.PolicyActionReject:
		allowed = false
		action = model.PolicyActionReject
	case model.PolicyActionBlockActivation:
		if changed, err := s.store.SetLicenseActivationBlocked(ctx, lic.ID, true); err != nil {
			s.logger.Error("block activations", "license_id", lic.ID, "error", err)
			action = "log"
		} else if changed {
			action = model.PolicyActionBlockActivation
			lic.ActivationBlocked = true
			s.auditStateChange(ctx, lic, ev, "activation_blocked", rule.ID)
		}
	case model.PolicyActionSuspend:
		if changed, err := s.store.SuspendLicenseForSecurity(ctx, lic.ID, rule.SuspendDays); err != nil {
			s.logger.Error("suspend license from security policy", "license_id", lic.ID, "error", err)
			action = "log"
		} else if changed {
			action = model.PolicyActionSuspend
			lic.Status = model.StatusSuspended
			s.auditStateChange(ctx, lic, ev, "suspended", rule.ID)
			s.dispatchLifecycle(ctx, lic, "license.suspended")
		} else {
			// Already suspended or not in a suspendable state; the
			// event is still evidence, so keep the flag.
			s.flag(ctx, lic, ev, "suspend not applicable")
			action = model.PolicyActionFlag
		}
	case model.PolicyActionRevoke:
		if changed, err := s.store.RevokeLicenseForSecurity(ctx, lic.ID); err != nil {
			s.logger.Error("revoke license from security policy", "license_id", lic.ID, "error", err)
			action = "log"
		} else if changed {
			action = model.PolicyActionRevoke
			lic.Status = model.StatusRevoked
			s.auditStateChange(ctx, lic, ev, "revoked", rule.ID)
			s.dispatchLifecycle(ctx, lic, "license.revoked")
		} else {
			action = model.PolicyActionFlag
		}
	case model.PolicyActionFlag:
		s.flag(ctx, lic, ev, "policy flag")
		action = model.PolicyActionFlag
	default:
		action = "log"
	}

	if action == model.PolicyActionSuspend || action == model.PolicyActionRevoke {
		s.flag(ctx, lic, ev, "policy action: "+action)
	}
	return action, rule.ID, allowed
}

// resolvePolicy picks the type-specific rule, then the product-wide
// `*` rule, then the built-in conservative default. A disabled row is
// treated as log-only rather than deleted: an operator can turn a rule
// off without losing its configuration.
func (s *SecurityService) resolvePolicy(ctx context.Context, productID, eventType string) model.SecurityPolicy {
	rules, err := s.store.ListSecurityPolicies(ctx, productID)
	if err != nil {
		s.logger.Error("load security policies", "product_id", productID, "error", err)
		return model.DefaultSecurityPolicy(eventType)
	}
	var fallback *model.SecurityPolicy
	for _, r := range rules {
		if r.EventType == eventType {
			return s.effectiveRule(*r, eventType)
		}
		if r.EventType == "*" {
			fallback = r
		}
	}
	if fallback != nil {
		return s.effectiveRule(*fallback, eventType)
	}
	return model.DefaultSecurityPolicy(eventType)
}

func (s *SecurityService) effectiveRule(r model.SecurityPolicy, eventType string) model.SecurityPolicy {
	if !r.Enabled || !model.ValidSecurityPolicyAction(r.Action) {
		return model.SecurityPolicy{
			ID: r.ID, ProductID: r.ProductID, EventType: eventType,
			Action: model.PolicyActionLog, Threshold: 1, WindowSeconds: 86400,
		}
	}
	return r
}

func (s *SecurityService) flag(ctx context.Context, lic *model.License, ev *model.SecurityEvent, reason string) {
	changed, err := s.store.MarkLicenseUnderReview(ctx, lic.ID, "security event: "+ev.EventType)
	if err != nil {
		s.logger.Error("flag license for review", "license_id", lic.ID, "error", err)
		return
	}
	if changed {
		lic.UnderReview = true
		s.store.Audit(ctx, &model.AuditLog{
			Entity: "license", EntityID: lic.ID, Action: "flagged_for_review",
			ActorType: "system", ActorID: "security-policy",
			Changes: map[string]any{
				"reason": reason, "event_type": ev.EventType, "event_id": ev.EventID,
			},
		})
	}
}

func (s *SecurityService) auditStateChange(ctx context.Context, lic *model.License, ev *model.SecurityEvent, action, ruleID string) {
	s.store.Audit(ctx, &model.AuditLog{
		Entity: "license", EntityID: lic.ID, Action: action,
		ActorType: "system", ActorID: "security-policy",
		Changes: map[string]any{
			"security_event_id": ev.ID,
			"event_id":          ev.EventID,
			"event_type":        ev.EventType,
			"policy_rule_id":    ruleID,
			"verified":          ev.Verified,
		},
	})
}

func (s *SecurityService) dispatchLifecycle(ctx context.Context, lic *model.License, event string) {
	if s.webhook == nil {
		return
	}
	s.webhook.Dispatch(ctx, lic.ProductID, event, map[string]any{
		"license_id": lic.ID, "email": lic.Email, "source": "security-policy",
	})
}

// ListPolicies returns explicit rules. The handler merges built-in
// defaults for the UI; the wire response stays a plain list.
func (s *SecurityService) ListPolicies(ctx context.Context, productID string) ([]*model.SecurityPolicy, error) {
	return s.store.ListSecurityPolicies(ctx, productID)
}

// ReplacePolicies validates and stores a complete rule set.
func (s *SecurityService) ReplacePolicies(ctx context.Context, productID string, rules []*model.SecurityPolicy) error {
	if len(rules) > 50 {
		return apperr.New(http.StatusBadRequest, "TOO_MANY_POLICIES", "at most 50 rules are allowed")
	}
	seen := make(map[string]bool, len(rules))
	for i, r := range rules {
		if err := model.ValidateSecurityPolicy(r); err != nil {
			return apperr.New(http.StatusBadRequest, "INVALID_SECURITY_POLICY",
				fmt.Sprintf("rule %d: %v", i, err))
		}
		if seen[r.EventType] {
			return apperr.New(http.StatusBadRequest, "DUPLICATE_SECURITY_POLICY",
				fmt.Sprintf("duplicate rule for event_type %q", r.EventType))
		}
		seen[r.EventType] = true
	}
	return s.store.ReplaceSecurityPolicies(ctx, productID, rules)
}

// AdminAction applies a state change chosen by an operator after
// reviewing a report. Unlike the automatic path this does not require
// a device signature: an authenticated admin is the authority.
func (s *SecurityService) AdminAction(ctx context.Context, eventID, action, reviewer string, suspendDays int) (*model.License, error) {
	ev, err := s.store.FindSecurityEvent(ctx, eventID)
	if err != nil {
		return nil, apperr.New(http.StatusNotFound, "SECURITY_EVENT_NOT_FOUND", "security event not found")
	}
	if !model.ValidSecurityPolicyAction(action) || action == model.PolicyActionReject {
		return nil, apperr.New(http.StatusBadRequest, "INVALID_SECURITY_ACTION",
			"action must be log, flag, block_activation, suspend or revoke")
	}
	if action == model.PolicyActionRevoke && ev.Status != model.SecurityEventStatusConfirmed {
		// Revocation is the only irreversible action here; require the
		// operator to confirm the report in a separate call first.
		return nil, apperr.New(http.StatusConflict, "EVENT_NOT_CONFIRMED",
			"confirm the security event before revoking the license")
	}

	lic, err := s.store.FindLicenseByID(ctx, ev.LicenseID)
	if err != nil {
		return nil, apperr.New(http.StatusNotFound, "LICENSE_NOT_FOUND", "license not found")
	}

	switch action {
	case model.PolicyActionLog:
		// nothing beyond the review marker
	case model.PolicyActionFlag:
		if _, err := s.store.MarkLicenseUnderReview(ctx, lic.ID, "admin review: "+ev.EventType); err != nil {
			return nil, apperr.Internal(err)
		}
	case model.PolicyActionBlockActivation:
		if _, err := s.store.SetLicenseActivationBlocked(ctx, lic.ID, true); err != nil {
			return nil, apperr.Internal(err)
		}
	case model.PolicyActionSuspend:
		if _, err := s.store.SuspendLicenseForSecurity(ctx, lic.ID, suspendDays); err != nil {
			return nil, apperr.Internal(err)
		}
	case model.PolicyActionRevoke:
		if _, err := s.store.RevokeLicenseForSecurity(ctx, lic.ID); err != nil {
			return nil, apperr.Internal(err)
		}
	}

	if err := s.store.ReviewSecurityEvent(ctx, ev.ID, model.SecurityEventStatusConfirmed, reviewer); err != nil {
		return nil, apperr.Internal(err)
	}
	// The audit action names the state change itself ("suspended",
	// "revoked", ...) so an operator searching the audit trail for a
	// lifecycle event finds it the same way whether a policy or a
	// person caused it. The report that justified it rides along in
	// Changes.
	auditAction := "security_event_reviewed"
	switch action {
	case model.PolicyActionBlockActivation:
		auditAction = "activation_blocked"
	case model.PolicyActionSuspend:
		auditAction = "suspended"
	case model.PolicyActionRevoke:
		auditAction = "revoked"
	case model.PolicyActionFlag:
		auditAction = "flagged_for_review"
	}
	s.store.Audit(ctx, &model.AuditLog{
		Entity: "license", EntityID: lic.ID, Action: auditAction,
		ActorType: "admin", ActorID: reviewer,
		Changes: map[string]any{
			"security_event_id": ev.ID,
			"event_id":          ev.EventID,
			"action":            action,
		},
	})
	return s.store.FindLicenseByID(ctx, lic.ID)
}

// Dismiss marks a report as not actionable. It never changes licence
// state; an operator can still clear a review flag separately through
// the license endpoints.
func (s *SecurityService) Dismiss(ctx context.Context, eventID, reviewer string) error {
	ev, err := s.store.FindSecurityEvent(ctx, eventID)
	if err != nil {
		return apperr.New(http.StatusNotFound, "SECURITY_EVENT_NOT_FOUND", "security event not found")
	}
	if err := s.store.ReviewSecurityEvent(ctx, ev.ID, model.SecurityEventStatusDismissed, reviewer); err != nil {
		return apperr.Internal(err)
	}
	s.store.Audit(ctx, &model.AuditLog{
		Entity: "license", EntityID: ev.LicenseID, Action: "security_event_dismissed",
		ActorType: "admin", ActorID: reviewer,
		Changes: map[string]any{"security_event_id": ev.ID, "event_id": ev.EventID},
	})
	return nil
}
