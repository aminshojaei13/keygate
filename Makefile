# Keygate Makefile

APP       := keygate
GO        := go
BUN       := bun
BIN       := bin/$(APP)
VERSION   ?= $(shell git describe --tags --always --dirty 2>/dev/null || echo "dev")
COMMIT    ?= $(shell git rev-parse --short HEAD 2>/dev/null || echo "unknown")
BUILD_DATE := $(shell date -u +%Y-%m-%dT%H:%M:%SZ)
LDFLAGS   := -X github.com/tabloy/keygate/internal/version.Version=$(VERSION) \
             -X github.com/tabloy/keygate/internal/version.Commit=$(COMMIT) \
             -X github.com/tabloy/keygate/internal/version.BuildDate=$(BUILD_DATE)

.PHONY: help
help: ## Show this help
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-16s\033[0m %s\n", $$1, $$2}'

# ─── Build ────────────────────────────────────────

.PHONY: build
build: build-web build-go ## Build everything

.PHONY: build-go
build-go: ## Build Go binary
	$(GO) build -trimpath -ldflags "$(LDFLAGS)" -o $(BIN) ./cmd/server

.PHONY: build-web
build-web: web/node_modules ## Build frontend
	cd web && $(BUN) run build

web/node_modules: web/package.json
	cd web && $(BUN) install
	@touch $@

# ─── Dev ──────────────────────────────────────────

.PHONY: dev
dev: ## Run backend with hot reload (air)
	air

.PHONY: dev-web
dev-web: web/node_modules ## Run frontend dev server
	cd web && $(BUN) run dev

# ─── Quality ──────────────────────────────────────

.PHONY: fmt
fmt: ## Format all code
	goimports -w ./cmd/ ./internal/ ./pkg/ 2>/dev/null || true
	gofmt -w ./cmd/ ./internal/ ./pkg/
	cd web && $(BUN) run fmt 2>/dev/null || true

.PHONY: lint
lint: ## Lint all code
	$(GO) vet ./...
	cd web && $(BUN) run lint 2>/dev/null || true

# One package at a time, as in CI: the integration tests share a database.
.PHONY: test
test: ## Run all tests (integration tests too when TEST_DATABASE_URL is set)
	$(GO) test -p 1 ./...

.PHONY: check
check: lint test build ## Full CI check (lint + test + build)

# ─── Database ─────────────────────────────────────

.PHONY: seed
seed: ## Seed database with demo data
	$(GO) run ./cmd/seed

.PHONY: db-backup
db-backup: ## Backup database from host environment
	pg_dump "$${DATABASE_URL}" --no-owner --no-privileges > backup-$$(date +%Y%m%d-%H%M%S).sql
	@echo "Backup saved to backup-*.sql"

.PHONY: db-restore
db-restore: ## Restore database from host environment (BACKUP=file.sql)
	@test -n "$(BACKUP)" || (echo "Usage: make db-restore BACKUP=backup.sql" && exit 1)
	psql "$${DATABASE_URL}" < $(BACKUP)
	@echo "Restored from $(BACKUP)"

# ─── Docker ───────────────────────────────────────

.PHONY: docker-build
docker-build: ## Build Docker image locally
	docker build -t $(APP):$(VERSION) \
		--build-arg VERSION=$(VERSION) \
		--build-arg COMMIT=$(COMMIT) \
		--build-arg BUILD_DATE=$(BUILD_DATE) .

.PHONY: docker-up
docker-up: ## Start with Docker Compose (production configuration)
	docker compose up -d

.PHONY: docker-dev
docker-dev: ## Start with Docker Compose (development override with build and exposed DB)
	docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --build

.PHONY: docker-migrate
docker-migrate: ## Run pending database migrations via container
	docker compose run --rm keygate migrate

.PHONY: docker-backup
docker-backup: ## Take a database snapshot from running Docker container
	@mkdir -p backups
	@OUT="backups/backup-$$(date +%Y%m%d-%H%M%S).sql"; \
	docker compose exec -T postgres pg_dump -U keygate --no-owner --no-privileges keygate > "$$OUT" && \
	echo "Docker database backup successfully saved to $$OUT"

.PHONY: docker-restore
docker-restore: ## Restore database in Docker container (BACKUP=backups/file.sql)
	@test -n "$(BACKUP)" || (echo "Usage: make docker-restore BACKUP=backups/file.sql" && exit 1)
	docker compose exec -T postgres psql -U keygate -d keygate < $(BACKUP)
	@echo "Restored container database from $(BACKUP)"

.PHONY: docker-down
docker-down: ## Stop Docker Compose
	docker compose down

.PHONY: docker-logs
docker-logs: ## Tail Docker Compose logs
	docker compose logs -f keygate

# ─── Clean ────────────────────────────────────────

.PHONY: clean
clean: ## Remove build artifacts
	rm -rf bin/ web/dist tmp/

.PHONY: clean-all
clean-all: clean ## Remove everything including node_modules
	rm -rf web/node_modules
