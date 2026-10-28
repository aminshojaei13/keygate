package service

import (
	"context"
	"crypto/ed25519"
	"database/sql"
	"errors"
	"fmt"
	"log/slog"
	"time"

	"github.com/tabloy/keygate/internal/branding"
	"github.com/tabloy/keygate/internal/license"
	"github.com/tabloy/keygate/internal/middleware"
	"github.com/tabloy/keygate/internal/model"
	"github.com/tabloy/keygate/internal/store"
	"github.com/tabloy/keygate/pkg/apperr"
	"github.com/tabloy/keygate/pkg/deviceid"
)

// FailureTracker tracks failed authentication attempts for brute-force protection.
type FailureTracker interface {
	RecordFailure(key string)
	RecordSuccess(key string)
	IsBlocked(key string) (bool, time.Duration)
}

// licenseSigningKey is satisfied by ed25519.PrivateKey. Aliased
// here so we don't widen the service's exported signature when the
// key format changes; the service only ever signs, never verifies.
type licenseSigningKey = ed25519.PrivateKey

type LicenseService struct {
	store      *store.Store
	signingKey licenseSigningKey
	logger     *slog.Logger
	failures   FailureTracker
	webhook    *WebhookService
	// strict binds a license to one device for its whole life. Any
	// identifier other than the active one is DEVICE_CHANGED, even if
	// the plan still has free activation slots. There is no client-side
	// release and no previous_identifier migration: only an admin reset
	// can move the license to new hardware.
	strict bool
}

func NewLicenseService(s *store.Store, signingKey licenseSigningKey, logger *slog.Logger, failures FailureTracker, webhook *WebhookService, strictDeviceBinding bool) *LicenseService {
	return &LicenseService{
		store:      s,
		signingKey: signingKey,
		logger:     logger,
		failures:   failures,
		webhook:    webhook,
		strict:     strictDeviceBinding,
	}
}

// strictSchemeSupported reports whether an identifier is derived from
// observable machine characteristics. Strict mode accepts:
//
//	kgid:v1:<platform>:<64-hex>  (composite of five component digests)
//	sha256:<64-hex>              (legacy monolithic hardware hash)
//
// uuid/opaque identifiers are usually random values stored on disk, so
// a hardware or OS change leaves them untouched. Accepting them in
// strict mode would silently defeat the whole check.
func strictSchemeSupported(identifier string) bool {
	scheme, _, _, err := deviceid.ParseIdentifier(identifier)
	if err != nil {
		return false
	}
	return scheme == deviceid.SchemeKGIDv1 || scheme == deviceid.SchemeSHA256
}

// deviceChanged is the single refusal for strict-mode rebinding. It
// deliberately does not echo the bound identifier: support can look it
// up in the admin panel, and a stolen license key should not learn what
// machine it is attached to.
func deviceChanged(reason string) *apperr.AppError {
	return apperr.WithDetails(
		apperr.Conflict("DEVICE_CHANGED", "device characteristics changed; this license is bound to another device"),
		map[string]any{
			"reason": reason,
			"action": "contact_support",
			"detail": "strict device binding is enabled; an administrator must reset the device binding before this license can run on new hardware",
		},
	)
}

func identifierSchemeError() *apperr.AppError {
	return apperr.WithDetails(
		apperr.New(400, "IDENTIFIER_SCHEME_UNSUPPORTED", "strict device binding requires a hardware-derived identifier"),
		map[string]any{
			"required_schemes": []string{deviceid.SchemeKGIDv1, deviceid.SchemeSHA256},
			"detail":           "use kgid:v1:<platform>:<64-hex> or sha256:<64-hex>; a random UUID cannot detect hardware or OS changes",
		},
	)
}

// SigningPublicKey returns the ed25519 public key that pairs with
// the service's signing key. Exposed so handlers can serve it on
// /api/v1/license/pubkey without poking at the private half.
func (s *LicenseService) SigningPublicKey() ed25519.PublicKey {
	return license.PublicKey(s.signingKey)
}

// ─── Activate ───

type ActivateInput struct {
	LicenseKey     string
	Identifier     string
	IdentifierType string // "device" | "user"
	Label          string
	IPAddress      string
	// ProductID: optional tenant scope. When non-empty the lookup
	// rejects licenses that don't belong to it. Public SDK endpoints
	// pass empty (license_key alone identifies tenant); server-to-server
	// integrations may pass the product ID for explicit verification.
	ProductID          string
	PreviousIdentifier string // optional: for seamless migration from an older identifier scheme
	// DevicePublicKey is an optional Ed25519 public key (base64) the
	// client generated locally. When supplied, the server binds it to
	// the activation and requires it to sign security-event reports.
	DevicePublicKey string
	DeviceKeyAlg    string
}

type ActivateResult struct {
	Status    string         `json:"status"` // "activated" | "already_activated"
	LicenseID string         `json:"license_id"`
	ProductID string         `json:"product_id"`
	Token     string         `json:"token"`
	Meta      map[string]any `json:"meta"`
}

func (s *LicenseService) Activate(ctx context.Context, in ActivateInput) (*ActivateResult, error) {
	if s.failures != nil {
		if blocked, retryAfter := s.failures.IsBlocked(middleware.FailureKeyIP(in.IPAddress)); blocked {
			return nil, lockedOut(retryAfter)
		}
	}

	if in.IdentifierType == "" {
		in.IdentifierType = "device"
	}

	lic, err := s.store.FindLicenseByKey(ctx, in.LicenseKey)
	if err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			if locked := s.recordFailure(in.IPAddress); locked != nil {
				return nil, locked
			}
			return nil, apperr.New(404, "LICENSE_NOT_FOUND", "license not found")
		}
		return nil, apperr.Internal(err)
	}

	if in.ProductID != "" && lic.ProductID != in.ProductID {
		if locked := s.recordFailure(in.IPAddress); locked != nil {
			return nil, locked
		}
		return nil, apperr.New(404, "LICENSE_NOT_FOUND", "license not found")
	}

	// Product-type capability gate: a SaaS product doesn't expose
	// per-device activation. Surfaces as 404 FEATURE_NOT_AVAILABLE so
	// the SDK call looks like the endpoint just doesn't exist for this
	// product — not a license-state failure the customer needs to fix.
	if err := requireProductCapability(lic.Product, model.CapActivations, "device activation"); err != nil {
		return nil, err
	}

	if err := s.assertUsable(lic); err != nil {
		s.store.Audit(ctx, &model.AuditLog{
			Entity: "license", EntityID: lic.ID, Action: "activation_failed",
			ActorType: "sdk", IPAddress: in.IPAddress,
			Changes: map[string]any{"identifier": in.Identifier, "reason": err.Error()},
		})
		return nil, err
	}

	if existing, err := s.store.FindActivation(ctx, lic.ID, in.Identifier); err == nil {
		_ = s.store.TouchActivation(ctx, existing.ID)
		if err := s.bindDeviceKey(ctx, lic, existing, in); err != nil {
			return nil, err
		}
		middleware.LicenseActivations.WithLabelValues(lic.ProductID, "already_activated").Inc()
		token, _, err := s.signToken(ctx, lic, in.Identifier)
		if err != nil {
			return nil, apperr.Internal(err)
		}
		return &ActivateResult{
			Status: "already_activated", LicenseID: lic.ID, ProductID: lic.ProductID,
			Token: token,
			Meta:  responseMeta(),
		}, nil
	}

	// A security policy can refuse new activations without touching
	// devices that already run. The same-device path above returned
	// first, so an existing install keeps refreshing its token.
	if lic.ActivationBlocked {
		s.store.Audit(ctx, &model.AuditLog{
			Entity: "license", EntityID: lic.ID, Action: "activation_blocked",
			ActorType: "sdk", IPAddress: in.IPAddress,
			Changes: map[string]any{"identifier": in.Identifier, "reason": "security_policy"},
		})
		return nil, apperr.WithDetails(
			apperr.New(403, "ACTIVATION_BLOCKED", "new activations are blocked for this license"),
			map[string]any{"action": "contact_support"},
		)
	}

	// Strict binding: a license is tied to the first device that
	// activates it. A second identifier is indistinguishable from the
	// same machine after a hardware or OS change, so it is refused
	// instead of silently consuming another slot. An administrator can
	// release the binding with POST /admin/licenses/:id/reset-device-binding.
	if s.strict {
		if !strictSchemeSupported(in.Identifier) {
			s.store.Audit(ctx, &model.AuditLog{
				Entity: "license", EntityID: lic.ID, Action: "activation_blocked",
				ActorType: "sdk", IPAddress: in.IPAddress,
				Changes: map[string]any{"identifier": in.Identifier, "reason": "identifier_scheme_unsupported"},
			})
			return nil, identifierSchemeError()
		}

		// previous_identifier was the old escape hatch for rebinding
		// after an identifier-algorithm change. It is exactly the
		// operation strict mode forbids.
		if in.PreviousIdentifier != "" && in.PreviousIdentifier != in.Identifier {
			s.store.Audit(ctx, &model.AuditLog{
				Entity: "license", EntityID: lic.ID, Action: "activation_blocked",
				ActorType: "sdk", IPAddress: in.IPAddress,
				Changes: map[string]any{
					"identifier":          in.Identifier,
					"previous_identifier": in.PreviousIdentifier,
					"reason":              "migration_disabled",
				},
			})
			return nil, deviceChanged("migration_disabled")
		}

		active, err := s.store.CountActivations(ctx, lic.ID)
		if err != nil {
			return nil, apperr.Internal(err)
		}
		if active > 0 {
			s.store.Audit(ctx, &model.AuditLog{
				Entity: "license", EntityID: lic.ID, Action: "activation_blocked",
				ActorType: "sdk", IPAddress: in.IPAddress,
				Changes: map[string]any{"identifier": in.Identifier, "reason": "different_device"},
			})
			return nil, deviceChanged("different_device")
		}
	}

	// Seamless version migration: if a client updates its identifier scheme
	// and supplies its active previous identifier, migrate the activation in place
	// without consuming an additional slot or returning ACTIVATION_LIMIT.
	// Strict mode never reaches this branch; it returned above.
	if !s.strict && in.PreviousIdentifier != "" && in.PreviousIdentifier != in.Identifier {
		newScheme := model.DetectIdentifierScheme(in.Identifier)
		migrated, mErr := s.store.MigrateActivationIdentifier(ctx, lic.ID, in.PreviousIdentifier, in.Identifier, newScheme)
		if mErr == nil && migrated != nil {
			s.store.Audit(ctx, &model.AuditLog{
				Entity: "license", EntityID: lic.ID, Action: "migrated",
				ActorType: "sdk", IPAddress: in.IPAddress,
				Changes: map[string]any{
					"from_identifier": in.PreviousIdentifier,
					"to_identifier":   in.Identifier,
					"scheme":          newScheme,
				},
			})
			middleware.LicenseActivations.WithLabelValues(lic.ProductID, "migrated").Inc()
			token, _, terr := s.signToken(ctx, lic, in.Identifier)
			if terr != nil {
				return nil, apperr.Internal(terr)
			}
			return &ActivateResult{
				Status:    "activated",
				LicenseID: lic.ID,
				ProductID: lic.ProductID,
				Token:     token,
				Meta:      responseMeta(),
			}, nil
		}
	}

	max := s.maxActivations(lic)
	if s.strict {
		// Even if the plan allows more seats, strict binding gives the
		// license exactly one live device. The transaction inside
		// ActivateWithinLimit serializes concurrent first activations,
		// so two devices racing cannot both win.
		max = 1
	}
	act := &model.Activation{
		LicenseID:       lic.ID,
		Identifier:      in.Identifier,
		IdentifierType:  in.IdentifierType,
		Scheme:          model.DetectIdentifierScheme(in.Identifier),
		Status:          model.ActivationActive,
		Label:           in.Label,
		IPAddress:       in.IPAddress,
		DevicePublicKey: in.DevicePublicKey,
		DeviceKeyAlg:    in.DeviceKeyAlg,
	}
	if err := s.store.ActivateWithinLimit(ctx, act, max); err != nil {
		// The machine already held a slot. The read above missed it
		// because it ran outside the transaction, so this is the same
		// answer that read would have given: a repeat activation is
		// the licence working, not a failure.
		if errors.Is(err, store.ErrAlreadyActivated) {
			_ = s.store.TouchActivation(ctx, act.ID)
			middleware.LicenseActivations.WithLabelValues(lic.ProductID, "already_activated").Inc()
			token, _, terr := s.signToken(ctx, lic, in.Identifier)
			if terr != nil {
				return nil, apperr.Internal(terr)
			}
			return &ActivateResult{
				Status: "already_activated", LicenseID: lic.ID, ProductID: lic.ProductID,
				Token: token,
				Meta:  responseMeta(),
			}, nil
		}
		if errors.Is(err, store.ErrActivationLimit) || err.Error() == "activation limit reached" {
			middleware.LicenseActivations.WithLabelValues(lic.ProductID, "failed").Inc()
			// Strict mode runs this path with max=1, so hitting the
			// limit means another device won the race — report the
			// strict refusal, not a plan-quota message the customer
			// would try to "fix" by upgrading their plan.
			if s.strict {
				s.store.Audit(ctx, &model.AuditLog{
					Entity: "license", EntityID: lic.ID, Action: "activation_blocked",
					ActorType: "sdk", IPAddress: in.IPAddress,
					Changes: map[string]any{"identifier": in.Identifier, "reason": "different_device"},
				})
				return nil, deviceChanged("different_device")
			}
			count, _ := s.store.CountActivations(ctx, lic.ID)
			s.store.Audit(ctx, &model.AuditLog{
				Entity: "license", EntityID: lic.ID, Action: "activation_failed",
				ActorType: "sdk", IPAddress: in.IPAddress,
				Changes: map[string]any{"identifier": in.Identifier, "reason": "activation_limit_reached"},
			})
			return nil, apperr.WithDetails(
				apperr.Conflict("ACTIVATION_LIMIT", "maximum activations reached"),
				map[string]any{"max": max, "current": count},
			)
		}
		return nil, apperr.Internal(err)
	}

	s.store.Audit(ctx, &model.AuditLog{
		Entity: "license", EntityID: lic.ID, Action: "activated",
		ActorType: "sdk", IPAddress: in.IPAddress,
		Changes: map[string]any{"identifier": in.Identifier, "type": in.IdentifierType, "label": in.Label},
	})

	if s.failures != nil {
		s.failures.RecordSuccess(middleware.FailureKeyIP(in.IPAddress))
	}

	s.logger.Info("license activated",
		"license_id", lic.ID, "identifier", in.Identifier, "type", in.IdentifierType)

	middleware.LicenseActivations.WithLabelValues(lic.ProductID, "activated").Inc()

	if s.webhook != nil {
		s.webhook.Dispatch(ctx, lic.ProductID, "license.activated", map[string]any{
			"license_id": lic.ID, "identifier": in.Identifier, "type": in.IdentifierType,
		})
	}

	token, _, err := s.signToken(ctx, lic, in.Identifier)
	if err != nil {
		return nil, apperr.Internal(err)
	}

	return &ActivateResult{
		Status: "activated", LicenseID: lic.ID, ProductID: lic.ProductID,
		Token: token,
		Meta:  responseMeta(),
	}, nil
}

// bindDeviceKey attaches the SDK's report-signing public key to an
// existing activation.
//
//   - No stored key + a key supplied: legacy activation upgrading to
//     signed reports. Record it.
//   - Stored key + no key supplied: the client lost its state (or is
//     an older SDK). Leave the binding alone; the next signed report
//     will prove possession.
//   - Two different keys for the same device slot: refuse. Accepting
//     a swap would let anyone holding the license key replace the key
//     and then file evidence in the device's name.
func (s *LicenseService) bindDeviceKey(ctx context.Context, lic *model.License, act *model.Activation, in ActivateInput) error {
	if in.DevicePublicKey == "" {
		return nil
	}
	if act.DevicePublicKey == "" {
		if err := s.store.UpdateActivationDeviceKey(ctx, act.ID, in.DevicePublicKey, in.DeviceKeyAlg); err != nil {
			return apperr.Internal(err)
		}
		act.DevicePublicKey = in.DevicePublicKey
		act.DeviceKeyAlg = in.DeviceKeyAlg
		s.store.Audit(ctx, &model.AuditLog{
			Entity: "license", EntityID: lic.ID, Action: "device_key_registered",
			ActorType: "sdk", IPAddress: in.IPAddress,
			Changes: map[string]any{"identifier": in.Identifier, "algorithm": in.DeviceKeyAlg},
		})
		return nil
	}
	if act.DevicePublicKey != in.DevicePublicKey {
		s.store.Audit(ctx, &model.AuditLog{
			Entity: "license", EntityID: lic.ID, Action: "device_key_mismatch",
			ActorType: "sdk", IPAddress: in.IPAddress,
			Changes: map[string]any{"identifier": in.Identifier},
		})
		return apperr.WithDetails(
			apperr.Conflict("DEVICE_KEY_MISMATCH", "a different report-signing key is already bound to this device"),
			map[string]any{"action": "contact_support"},
		)
	}
	return nil
}

// recordFailure files a failed attempt and reports the lockout when
// this was the attempt that caused one. The guard middleware turns
// away everything after it, but the request that crosses the line is
// answered from here — and a plain 404 tells a client nothing about
// the wall it has just walked into, so it comes straight back.
func (s *LicenseService) recordFailure(ip string) *apperr.AppError {
	if s.failures == nil {
		return nil
	}
	key := middleware.FailureKeyIP(ip)
	s.failures.RecordFailure(key)
	if blocked, retryAfter := s.failures.IsBlocked(key); blocked {
		return lockedOut(retryAfter)
	}
	return nil
}

// lockedOut is the refusal a caller meets while their address is
// locked out. It carries how long to wait: the guard middleware sets
// Retry-After when it turns a request away on its own, but the
// request that trips the lock is answered from here, and a client
// told only "too many failed attempts" retries straight away.
func lockedOut(retryAfter time.Duration) *apperr.AppError {
	return apperr.WithDetails(
		apperr.New(429, "LOCKED_OUT", "too many failed attempts"),
		map[string]any{"retry_after": middleware.RetryAfterSeconds(retryAfter)},
	)
}

// ─── Verify ───

type VerifyInput struct {
	LicenseKey string
	Identifier string
	ProductID  string
	IPAddress  string
}

type VerifyResult struct {
	Status     string     `json:"status"`
	ProductID  string     `json:"product_id"`
	PlanID     string     `json:"plan_id"`
	PlanName   string     `json:"plan_name"`
	ValidUntil *time.Time `json:"valid_until,omitempty"`
	// UpdatesUntil is the end of a perpetual license's maintenance
	// period: releases published after it cannot be installed. Absent
	// when the license has no separate limit.
	UpdatesUntil *time.Time     `json:"updates_until,omitempty"`
	Features     map[string]any `json:"features"`
	Token        string         `json:"token"`
	GraceDays    int            `json:"grace_days"`
	Meta         map[string]any `json:"meta"`
	// External identifiers echoed back so the SDK can confirm the
	// license belongs to the workspace it expects. Empty when the
	// license was created without them. Optional in the JSON envelope
	// to keep the payload small for the common case.
	ExternalCustomerID  string `json:"external_customer_id,omitempty"`
	ExternalWorkspaceID string `json:"external_workspace_id,omitempty"`
}

func (s *LicenseService) Verify(ctx context.Context, in VerifyInput) (*VerifyResult, error) {
	if s.failures != nil {
		if blocked, retryAfter := s.failures.IsBlocked(middleware.FailureKeyIP(in.IPAddress)); blocked {
			return nil, lockedOut(retryAfter)
		}
	}

	lic, err := s.store.FindLicenseByKey(ctx, in.LicenseKey)
	if err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			if locked := s.recordFailure(in.IPAddress); locked != nil {
				return nil, locked
			}
			return nil, apperr.New(404, "LICENSE_NOT_FOUND", "license not found")
		}
		return nil, apperr.Internal(err)
	}

	if in.ProductID != "" && lic.ProductID != in.ProductID {
		if locked := s.recordFailure(in.IPAddress); locked != nil {
			return nil, locked
		}
		return nil, apperr.New(404, "LICENSE_NOT_FOUND", "license not found")
	}

	// Verify is part of the activation flow — also gated by product type.
	if err := requireProductCapability(lic.Product, model.CapActivations, "device verification"); err != nil {
		return nil, err
	}

	// Verify is unauthenticated. We collapse all "license-knowable" failures
	// (suspended / revoked / expired / wrong device) into the same 404 the
	// non-existent-key path returns, so the endpoint can't be used to probe
	// which license_key strings are real. Paid users learn lifecycle state
	// via email + the (session-auth) portal, not via this routine call.
	if err := s.assertUsable(lic); err != nil {
		locked := s.recordFailure(in.IPAddress)
		middleware.LicenseVerifications.WithLabelValues(lic.ProductID, "unusable").Inc()
		if locked != nil {
			return nil, locked
		}
		return nil, licenseNotFound()
	}

	act, err := s.store.FindActivation(ctx, lic.ID, in.Identifier)
	if err != nil {
		middleware.LicenseVerifications.WithLabelValues(lic.ProductID, "not_activated").Inc()
		// Not yet activated for this device → looks identical to "no
		// such license". Otherwise this is the loudest existence
		// oracle: 403 NOT_ACTIVATED tells the attacker a key is real.
		return nil, licenseNotFound()
	}
	_ = s.store.TouchActivation(ctx, act.ID)

	if s.failures != nil {
		s.failures.RecordSuccess(middleware.FailureKeyIP(in.IPAddress))
	}

	planName := ""
	if lic.Plan != nil {
		planName = lic.Plan.Name
	}

	middleware.LicenseVerifications.WithLabelValues(lic.ProductID, "valid").Inc()

	token, features, err := s.signToken(ctx, lic, in.Identifier)
	if err != nil {
		return nil, apperr.Internal(err)
	}

	return &VerifyResult{
		Status:              lic.Status,
		ProductID:           lic.ProductID,
		PlanID:              lic.PlanID,
		PlanName:            planName,
		ValidUntil:          lic.ValidUntil,
		UpdatesUntil:        lic.EffectiveUpdatesUntil(),
		Features:            features,
		Token:               token,
		GraceDays:           s.effectiveGraceDays(lic),
		Meta:                responseMeta(),
		ExternalCustomerID:  lic.ExternalCustomerID,
		ExternalWorkspaceID: lic.ExternalWorkspaceID,
	}, nil
}

// ─── Deactivate ───

type DeactivateInput struct {
	LicenseKey string
	Identifier string
	ProductID  string
	IPAddress  string
}

func (s *LicenseService) Deactivate(ctx context.Context, in DeactivateInput) error {
	if s.failures != nil {
		if blocked, retryAfter := s.failures.IsBlocked(middleware.FailureKeyIP(in.IPAddress)); blocked {
			return lockedOut(retryAfter)
		}
	}

	lic, err := s.store.FindLicenseByKey(ctx, in.LicenseKey)
	if err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			if locked := s.recordFailure(in.IPAddress); locked != nil {
				return locked
			}
			return apperr.New(404, "LICENSE_NOT_FOUND", "license not found")
		}
		return apperr.Internal(err)
	}

	if in.ProductID != "" && lic.ProductID != in.ProductID {
		if locked := s.recordFailure(in.IPAddress); locked != nil {
			return locked
		}
		return apperr.New(404, "LICENSE_NOT_FOUND", "license not found")
	}

	if err := requireProductCapability(lic.Product, model.CapActivations, "device deactivation"); err != nil {
		return err
	}

	act, err := s.store.FindActivation(ctx, lic.ID, in.Identifier)
	if err != nil {
		// Same 404 LICENSE_NOT_FOUND as "license missing" so deactivate
		// can't be used to probe for real keys ("oh, this key returned
		// ACTIVATION_NOT_FOUND, that's specific — must exist").
		return licenseNotFound()
	}

	// Strict binding removes the self-service reset. A client could
	// otherwise release its slot and activate a different machine with
	// the same license key, which is the exact bypass strict mode
	// exists to close. Support releases the binding from the admin API.
	if s.strict {
		s.store.Audit(ctx, &model.AuditLog{
			Entity: "license", EntityID: lic.ID, Action: "deactivation_blocked",
			ActorType: "sdk", IPAddress: in.IPAddress,
			Changes: map[string]any{"identifier": in.Identifier, "reason": "strict_device_binding"},
		})
		return apperr.New(403, "DEACTIVATION_DISABLED",
			"strict device binding is enabled; an administrator must release this device")
	}

	if _, err := s.store.SoftDeactivateActivation(ctx, act.ID, "client"); err != nil {
		return apperr.Internal(err)
	}

	if s.failures != nil {
		s.failures.RecordSuccess(middleware.FailureKeyIP(in.IPAddress))
	}

	s.store.Audit(ctx, &model.AuditLog{
		Entity: "license", EntityID: lic.ID, Action: "deactivated",
		ActorType: "sdk", IPAddress: in.IPAddress,
		Changes: map[string]any{"identifier": in.Identifier, "reason": "client"},
	})

	s.logger.Info("license deactivated", "license_id", lic.ID, "identifier", in.Identifier)

	if s.webhook != nil {
		s.webhook.Dispatch(ctx, lic.ProductID, "license.deactivated", map[string]any{
			"license_id": lic.ID, "identifier": in.Identifier,
		})
	}

	return nil
}

// ─── Helpers ───

// licenseNotFound returns a generic 404 used for unauthenticated public
// SDK responses. We deliberately do NOT distinguish between
//
//   - the license_key doesn't exist
//   - the license belongs to another product
//   - the license is suspended / revoked / expired / past-grace
//
// because all of those reveal the EXISTENCE of a real key. The portal
// (session-authenticated) and admin endpoints surface the specific
// status; SDK clients learn details via email or by checking the portal.
func licenseNotFound() error {
	return apperr.New(404, "LICENSE_NOT_FOUND", "license not found")
}

// loadLicenseForSDK is the canonical license-lookup pattern for
// unauthenticated, license-key-bearing SDK endpoints (download,
// usage, seats, floating, entitlements). It performs every check
// (key lookup → product scope → product capability → lifecycle
// usability) and collapses every failure into the same 404
// LICENSE_NOT_FOUND so the endpoint surface can't be used to probe
// which license keys are real, which products own them, or which
// lifecycle state they're in.
//
//   - capability == "": skip the capability check
//   - usabilityRequired == false: skip the lifecycle check (use for
//     read-only endpoints that should still respond to suspended
//     licenses; today every caller passes true)
//
// Admin endpoints (session-authenticated) MUST NOT use this helper —
// they need specific reasons (LICENSE_EXPIRED, INCOMPATIBLE_PRODUCT_TYPE,
// etc.) to drive UI affordances.
func loadLicenseForSDK(ctx context.Context, st *store.Store, key, productID, capability string, usabilityRequired bool) (*model.License, error) {
	lic, err := st.FindLicenseByKey(ctx, key)
	if err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, licenseNotFound()
		}
		return nil, apperr.Internal(err)
	}
	if productID != "" && lic.ProductID != productID {
		return nil, licenseNotFound()
	}
	if capability != "" {
		if lic.Product == nil || !model.ProductSupports(lic.Product.Type, capability) {
			return nil, licenseNotFound()
		}
	}
	if usabilityRequired && !IsLicenseUsable(lic) {
		return nil, licenseNotFound()
	}
	return lic, nil
}

// requireProductCapability is the central guard for product-type
// capability gates. It returns nil when the product supports the
// requested capability, or a 404 FEATURE_NOT_AVAILABLE error when it
// doesn't. We use 404 instead of 400 because from the caller's
// perspective the endpoint simply doesn't exist for this product —
// matching how a missing route would look.
func requireProductCapability(prod *model.Product, capability, hint string) error {
	if prod == nil {
		return apperr.Internal(fmt.Errorf("product not loaded for capability check"))
	}
	if model.ProductSupports(prod.Type, capability) {
		return nil
	}
	return apperr.New(404, "FEATURE_NOT_AVAILABLE",
		fmt.Sprintf("%s is not available for %s products", hint, prod.Type))
}

// assertUsable checks lifecycle status — usable now or not. The returned
// error is the specific reason (LICENSE_EXPIRED / CANCELED / etc.); the
// caller decides whether to surface it (Activate path: yes, paid users
// renewing) or collapse to licenseNotFound() (Verify / Deactivate: no,
// avoid existence oracle).
// LicenseUsable reports whether a licence's key works right now — the
// rule activation and verification apply — for callers outside this
// service: the admin resend of a key mails only one that works. It reads
// nothing but the licence and its plan, and assertUsable must stay that
// way for this to hold.
func LicenseUsable(lic *model.License) error {
	var s LicenseService
	return s.assertUsable(lic)
}

func (s *LicenseService) assertUsable(lic *model.License) error {
	now := time.Now()
	switch lic.Status {
	case model.StatusActive, model.StatusTrialing, model.StatusPastDue:
		if lic.ValidUntil != nil && now.After(*lic.ValidUntil) {
			grace := time.Duration(s.effectiveGraceDays(lic)) * 24 * time.Hour
			if now.After(lic.ValidUntil.Add(grace)) {
				return apperr.New(403, "LICENSE_EXPIRED", "license has expired")
			}
		}
		return nil
	case model.StatusCanceled:
		if lic.ValidUntil != nil && now.Before(*lic.ValidUntil) {
			return nil
		}
		return apperr.New(403, "LICENSE_CANCELED", "license has been canceled")
	case model.StatusSuspended:
		// A timed security suspension lifts itself when it expires.
		// The row still says suspended so the dashboard can show why
		// it happened; only the usability decision changes.
		if lic.SuspendedUntil != nil && now.After(*lic.SuspendedUntil) {
			return nil
		}
		return apperr.New(403, "LICENSE_SUSPENDED", "license has been suspended")
	case model.StatusRevoked:
		return apperr.New(403, "LICENSE_REVOKED", "license has been revoked")
	case model.StatusExpired:
		return apperr.New(403, "LICENSE_EXPIRED", "license has expired")
	default:
		return apperr.New(403, "LICENSE_INVALID", "license is not valid")
	}
}

func (s *LicenseService) maxActivations(lic *model.License) int {
	if lic.Plan != nil {
		return lic.Plan.MaxActivations
	}
	return 3
}

func (s *LicenseService) graceDays(lic *model.License) int {
	if lic.Plan != nil {
		return lic.Plan.GraceDays
	}
	return 7
}

// effectiveGraceDays is the grace this licence actually gets, which is
// not always the plan's number: a canceled licence is paid up to
// ValidUntil and stops dead there.
//
// assertUsable, the signed token and the verify envelope must all read
// the same value. When they drift, the client is told a grace period
// the server will not honour — it keeps working offline for a week
// while every online call already returns LICENSE_CANCELED.
func (s *LicenseService) effectiveGraceDays(lic *model.License) int {
	switch lic.Status {
	case model.StatusActive, model.StatusTrialing, model.StatusPastDue:
		return s.graceDays(lic)
	default:
		return 0
	}
}

func responseMeta() map[string]any {
	return map[string]any{"server": branding.Project, "url": branding.URL}
}

// tokenTTL is the default check-in interval: how long a signed token
// stays usable offline before the client has to verify again. A plan
// can override it with token_ttl_days. It is a check-in interval, not
// a licence term — see the clamp below.
const tokenTTL = 7 * 24 * time.Hour

func (s *LicenseService) tokenTTLFor(lic *model.License) time.Duration {
	if lic.Plan != nil && lic.Plan.TokenTTLDays > 0 {
		return time.Duration(lic.Plan.TokenTTLDays) * 24 * time.Hour
	}
	return tokenTTL
}

// signToken also returns the feature map it signed, so a verify answer
// reports exactly the features its token carries.
func (s *LicenseService) signToken(ctx context.Context, lic *model.License, identifier string) (string, map[string]any, error) {
	now := time.Now()

	ents, err := licenseFeatures(ctx, s.store, lic)
	if err != nil {
		return "", nil, err
	}
	features := featureValues(ents)

	// A token must never outlive the licence it was issued for. With a
	// flat 7-day TTL, a plan whose grace period is shorter than that
	// handed out tokens that kept verifying offline after the licence
	// was truly dead: valid_until tomorrow + grace_days 0 still bought
	// six extra days. Clamp to whichever comes first.
	expiresAt := now.Add(s.tokenTTLFor(lic))

	// Grace comes from effectiveGraceDays so the token, the envelope
	// around it and assertUsable cannot disagree about when this
	// licence dies.
	grace := s.effectiveGraceDays(lic)

	var validUntil int64
	if lic.ValidUntil != nil {
		validUntil = lic.ValidUntil.Unix()
		deadline := lic.ValidUntil.Add(time.Duration(grace) * 24 * time.Hour)
		if deadline.Before(expiresAt) {
			expiresAt = deadline
		}
	}

	var updatesUntil int64
	if u := lic.EffectiveUpdatesUntil(); u != nil {
		updatesUntil = u.Unix()
	}

	t := &license.VerifyToken{
		LicenseID:    lic.ID,
		ProductID:    lic.ProductID,
		PlanID:       lic.PlanID,
		Status:       lic.Status,
		Identifier:   identifier,
		Features:     features,
		IssuedAt:     now.Unix(),
		ExpiresAt:    expiresAt.Unix(),
		ValidUntil:   validUntil,
		UpdatesUntil: updatesUntil,
		GraceDays:    grace,
		Fingerprint:  license.Fingerprint(identifier, lic.ProductID),
	}
	token, err := license.Sign(t, s.signingKey)
	return token, features, err
}
