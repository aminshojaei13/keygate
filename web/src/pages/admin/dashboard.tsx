import { useQuery } from "@tanstack/react-query"
import {
  Activity,
  ArrowRight,
  ArrowUpCircle,
  CheckCircle2,
  Key,
  Lock,
  Package,
  ScrollText,
  ShieldCheck,
  Users,
} from "lucide-react"
import { Link } from "react-router-dom"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import {
  DataTable,
  DataTableBody,
  DataTableCell,
  DataTableHead,
  DataTableHeader,
  DataTableRow,
} from "@/components/ui/data-table"
import { type TranslationKeys, useI18n } from "@/i18n"
import { admin } from "@/lib/api"
import { formatDate, statusColor } from "@/lib/utils"

export default function DashboardPage() {
  const { t } = useI18n()

  const { data: stats, isLoading: loadingStats } = useQuery({
    queryKey: ["admin", "stats"],
    queryFn: admin.stats,
  })

  const { data: versionData } = useQuery({
    queryKey: ["admin", "version"],
    queryFn: admin.getVersion,
  })

  const { data: auditData } = useQuery({
    queryKey: ["admin", "dashboard-audit"],
    queryFn: () => admin.listAuditLogs({ limit: 5 }),
  })

  const { data: updateData } = useQuery({
    queryKey: ["admin", "update-check"],
    queryFn: admin.checkUpdate,
    staleTime: 60 * 60 * 1000,
  })

  if (loadingStats || !stats) {
    return (
      <div className="animate-pulse space-y-4">
        <div className="h-32 bg-muted rounded-lg" />
        <div className="h-32 bg-muted rounded-lg" />
      </div>
    )
  }

  const cards = [
    { label: t("dashboard.totalLicenses"), value: stats.total_licenses, icon: Key, color: "text-primary" },
    { label: t("dashboard.activeLicenses"), value: stats.active_licenses, icon: Activity, color: "text-emerald-600" },
    { label: t("dashboard.totalActivations"), value: stats.total_activations, icon: Users, color: "text-primary/70" },
    { label: t("dashboard.products"), value: stats.total_products, icon: Package, color: "text-amber-600" },
  ]

  const auditLogs = auditData?.audit_logs || []

  const actionColor = (action: string) => {
    if (action === "created" || action === "activated" || action === "reactivated") {
      return "bg-emerald-100 text-emerald-800"
    }
    if (action === "deleted" || action === "revoked") {
      return "bg-red-100 text-red-800"
    }
    if (action === "suspended" || action === "deactivated") {
      return "bg-amber-100 text-amber-800"
    }
    return "bg-blue-100 text-blue-800"
  }

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-bold tracking-tight sr-only md:not-sr-only">{t("dashboard.title")}</h1>
        <p className="text-muted-foreground">{t("dashboard.subtitle")}</p>
      </div>

      {updateData?.available && (
        <div className="flex items-center justify-between rounded-lg border border-blue-200 bg-blue-50 px-4 py-3">
          <div className="flex items-center gap-3">
            <ArrowUpCircle className="h-5 w-5 text-blue-600" />
            <div>
              <p className="text-sm font-medium text-blue-900">
                {t("dashboard.updateAvailable", { version: updateData.latest_version })}
              </p>
              {updateData.update_command && (
                <code className="text-xs text-blue-700 font-mono mt-0.5 block">{updateData.update_command}</code>
              )}
            </div>
          </div>
          <div className="flex items-center gap-2">
            <Link to="/admin/settings?tab=system">
              <Button size="sm" variant="outline" className="text-blue-700 border-blue-300">
                {t("dashboard.viewUpdate")}
              </Button>
            </Link>
            {updateData.release_url && (
              <a href={updateData.release_url} target="_blank" rel="noopener noreferrer">
                <Button size="sm" className="bg-blue-600 hover:bg-blue-700">
                  {t("dashboard.releaseNotes")}
                </Button>
              </a>
            )}
          </div>
        </div>
      )}

      {/* Top Metric Cards */}
      <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-4">
        {cards.map((c) => (
          <Card key={c.label}>
            <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
              <CardTitle className="text-sm font-medium">{c.label}</CardTitle>
              <c.icon className={`h-4 w-4 ${c.color}`} />
            </CardHeader>
            <CardContent>
              <div className="text-3xl font-bold">{c.value}</div>
            </CardContent>
          </Card>
        ))}
      </div>

      {/* System Health and Status Breakdown Row */}
      <div className="grid gap-6 md:grid-cols-1 lg:grid-cols-3">
        {/* Status Breakdown (2 cols) */}
        <Card className="lg:col-span-2">
          <CardHeader className="flex flex-row items-center justify-between space-y-0">
            <CardTitle className="text-base">{t("dashboard.statusDistribution")}</CardTitle>
            <Link to="/admin/licenses" className="text-xs text-primary hover:underline flex items-center gap-1">
              {t("nav.licenses")} <ArrowRight className="h-3 w-3" />
            </Link>
          </CardHeader>
          <CardContent>
            {Object.keys(stats.by_status).length === 0 ? (
              <p className="text-xs text-muted-foreground py-4">No licenses recorded yet.</p>
            ) : (
              <div className="grid grid-cols-2 sm:grid-cols-3 gap-3">
                {Object.entries(stats.by_status).map(([status, count]) => (
                  <div key={status} className="border rounded-lg p-3 flex flex-col justify-between">
                    <div className="flex items-center justify-between">
                      <Badge className={statusColor(status)}>{t(`status.${status}` as TranslationKeys)}</Badge>
                    </div>
                    <div className="text-2xl font-bold mt-2">{count}</div>
                  </div>
                ))}
              </div>
            )}
          </CardContent>
        </Card>

        {/* API Service & Security Health (1 col) */}
        <Card>
          <CardHeader>
            <CardTitle className="text-base flex items-center gap-2">
              <ShieldCheck className="h-4 w-4 text-emerald-600" />
              {t("dashboard.systemHealth")}
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-4">
            <div className="flex items-center gap-2">
              <CheckCircle2 className="h-4 w-4 text-emerald-600 shrink-0" />
              <span className="text-sm font-medium text-emerald-700">{t("dashboard.apiOnline")}</span>
            </div>

            <div className="border rounded-lg p-3 space-y-2 text-xs bg-muted/30">
              <div className="flex justify-between">
                <span className="text-muted-foreground">Version:</span>
                <span className="font-mono font-semibold">{versionData?.version || "dev"}</span>
              </div>
              <div className="flex justify-between">
                <span className="text-muted-foreground">Commit:</span>
                <span className="font-mono">{versionData?.commit?.slice(0, 7) || "local"}</span>
              </div>
              <div className="flex justify-between">
                <span className="text-muted-foreground">{t("dashboard.encryptionStatus")}:</span>
                <span className="text-emerald-700 font-medium flex items-center gap-1">
                  <Lock className="h-3 w-3" /> Active
                </span>
              </div>
            </div>

            <div className="pt-1">
              <Link to="/admin/settings" className="text-xs text-primary hover:underline flex items-center gap-1">
                Manage system configuration <ArrowRight className="h-3 w-3" />
              </Link>
            </div>
          </CardContent>
        </Card>
      </div>

      {/* Recent Security & Activation Audit Events */}
      <Card>
        <CardHeader className="flex flex-row items-center justify-between space-y-0">
          <CardTitle className="text-base flex items-center gap-2">
            <ScrollText className="h-4 w-4" />
            {t("dashboard.securityEvents")}
          </CardTitle>
          <Link to="/admin/audit" className="text-xs text-primary hover:underline flex items-center gap-1">
            {t("dashboard.viewAllAudit")} <ArrowRight className="h-3 w-3" />
          </Link>
        </CardHeader>
        <CardContent>
          {auditLogs.length === 0 ? (
            <p className="text-xs text-muted-foreground py-4 text-center">No recent security events logged.</p>
          ) : (
            <DataTable>
              <DataTableHeader>
                <DataTableRow>
                  <DataTableHead>Timestamp</DataTableHead>
                  <DataTableHead>Entity</DataTableHead>
                  <DataTableHead>Action</DataTableHead>
                  <DataTableHead>Actor</DataTableHead>
                  <DataTableHead>Target ID</DataTableHead>
                </DataTableRow>
              </DataTableHeader>
              <DataTableBody>
                {auditLogs.map((log) => (
                  <DataTableRow key={log.id}>
                    <DataTableCell className="text-xs text-muted-foreground">
                      {formatDate(log.created_at)}
                    </DataTableCell>
                    <DataTableCell>
                      <Badge variant="outline" className="text-[10px] uppercase font-mono">
                        {log.entity}
                      </Badge>
                    </DataTableCell>
                    <DataTableCell>
                      <Badge className={`text-[10px] ${actionColor(log.action)}`}>
                        {log.action}
                      </Badge>
                    </DataTableCell>
                    <DataTableCell className="text-xs">
                      <span className="text-muted-foreground">{log.actor_type}</span>
                    </DataTableCell>
                    <DataTableCell className="text-xs font-mono text-muted-foreground max-w-[16ch] truncate" title={log.entity_id}>
                      {log.entity_id || "-"}
                    </DataTableCell>
                  </DataTableRow>
                ))}
              </DataTableBody>
            </DataTable>
          )}
        </CardContent>
      </Card>

      {/* Recent licenses */}
      {stats.recent_licenses?.length > 0 && (
        <Card>
          <CardHeader className="flex flex-row items-center justify-between space-y-0">
            <CardTitle className="text-base">{t("dashboard.recentLicenses")}</CardTitle>
            <Link to="/admin/licenses" className="text-xs text-primary hover:underline flex items-center gap-1">
              View all licenses <ArrowRight className="h-3 w-3" />
            </Link>
          </CardHeader>
          <CardContent>
            <DataTable>
              <DataTableHeader>
                <DataTableRow>
                  <DataTableHead>{t("common.email")}</DataTableHead>
                  <DataTableHead>{t("common.product")}</DataTableHead>
                  <DataTableHead>{t("common.plan")}</DataTableHead>
                  <DataTableHead>{t("common.status")}</DataTableHead>
                  <DataTableHead>{t("common.created")}</DataTableHead>
                </DataTableRow>
              </DataTableHeader>
              <DataTableBody>
                {stats.recent_licenses.map((lic) => (
                  <DataTableRow key={lic.id}>
                    <DataTableCell>
                      <Link to={`/admin/licenses?id=${lic.id}`} className="font-medium hover:underline">
                        {lic.email}
                      </Link>
                    </DataTableCell>
                    <DataTableCell className="text-muted-foreground">{lic.product?.name || "-"}</DataTableCell>
                    <DataTableCell className="text-muted-foreground">{lic.plan?.name || "-"}</DataTableCell>
                    <DataTableCell>
                      <Badge className={statusColor(lic.status)}>{t(`status.${lic.status}` as TranslationKeys)}</Badge>
                    </DataTableCell>
                    <DataTableCell className="text-muted-foreground text-xs">
                      {formatDate(lic.created_at)}
                    </DataTableCell>
                  </DataTableRow>
                ))}
              </DataTableBody>
            </DataTable>
          </CardContent>
        </Card>
      )}
    </div>
  )
}
