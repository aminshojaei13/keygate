package store

import (
	"errors"
	"strings"

	"github.com/uptrace/bun/driver/pgdriver"
)

// isUniqueViolation reports whether err is a Postgres unique-constraint
// violation (SQLSTATE 23505). Uses pgdriver.Error for type-safe inspection
// rather than substring matching on the human-readable message.
//
// Driver-specific: relies on uptrace/bun pgdriver. If the project ever
// migrates to bun's pgxdialect (or another driver), this check must be
// revisited.
func isUniqueViolation(err error) bool {
	if err == nil {
		return false
	}
	var pgErr pgdriver.Error
	if errors.As(err, &pgErr) {
		return pgErr.Field('C') == "23505"
	}
	return false
}

// isUniqueViolationNamed reports whether err is a Postgres unique_violation
// (SQLSTATE 23505) raised by an index or constraint whose name contains name.
func isUniqueViolationNamed(err error, name string) bool {
	if !isUniqueViolation(err) {
		return false
	}
	if name == "" {
		return true
	}
	var pgErr pgdriver.Error
	if errors.As(err, &pgErr) {
		return strings.Contains(pgErr.Field('n'), name) || strings.Contains(err.Error(), name)
	}
	return strings.Contains(err.Error(), name)
}

