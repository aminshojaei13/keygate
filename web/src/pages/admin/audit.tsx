import { useQuery } from "@tanstack/react-query"
import { Check, Copy, Eye } from "lucide-react"
import { useState } from "react"
import { ProductSelect } from "@/components/product-select"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Card, CardContent } from "@/components/ui/card"
import {
  DataTable,
  DataTableBody,
  DataTableCell,
  DataTableEmpty,
  DataTableHead,
  DataTableHeader,
  DataTablePagination,
  DataTableRow,
} from "@/components/ui/data-table"
import {
  Dialog,
  DialogBody,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog"
import { Input } from "@/components/ui/input"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { useI18n } from "@/i18n"
import { admin, type AuditLog } from "@/lib/api"
import { formatDate } from "@/lib/utils"

export default function AuditPage() {
  const { t } = useI18n()
  const [entityFilter, setEntityFilter] = useState("")
  const [entityIdFilter, setEntityIdFilter] = useState("")
  const [productFilter, setProductFilter] = useState("")
  const [page, setPage] = useState(0)
  const [selectedLog, setSelectedLog] = useState<AuditLog | null>(null)
  const [copiedField, setCopiedField] = useState<string | null>(null)
  const limit = 30

  const copyToClipboard = (text: string, field: string) => {
    navigator.clipboard.writeText(text)
    setCopiedField(field)
    setTimeout(() => setCopiedField(null), 2000)
  }

  const { data, isLoading } = useQuery({
    queryKey: ["admin", "audit", entityFilter, entityIdFilter, productFilter, page],
    queryFn: () =>
      admin.listAuditLogs({
        entity: entityFilter || undefined,
        entity_id: entityIdFilter || undefined,
        product_id: productFilter || undefined,
        offset: page * limit,
        limit,
      }),
  })

  const logs = data?.audit_logs || []
  const total = data?.total || 0
  const totalPages = Math.ceil(total / limit)

  const actionColor = (action: string) => {
    if (action === "created") return "bg-emerald-100 text-emerald-800"
    if (action === "deleted" || action === "revoked") return "bg-red-100 text-red-800"
    if (action === "suspended") return "bg-orange-100 text-orange-800"
    return "bg-blue-100 text-blue-800"
  }

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-bold tracking-tight sr-only md:not-sr-only">{t("audit.title")}</h1>
        <p className="text-muted-foreground">{t("audit.subtitle")}</p>
      </div>

      <div className="flex flex-wrap gap-3">
        <Select
          value={entityFilter || "all"}
          onValueChange={(v) => {
            setEntityFilter(v === "all" ? "" : v)
            setPage(0)
          }}
        >
          <SelectTrigger className="w-full sm:w-48">
            <SelectValue placeholder={t("audit.filterEntity")} />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="all">{t("audit.allEntities")}</SelectItem>
            <SelectItem value="license">License</SelectItem>
            <SelectItem value="activation">Activation / Device</SelectItem>
            <SelectItem value="product">Product</SelectItem>
            <SelectItem value="plan">Plan</SelectItem>
            <SelectItem value="release">Release</SelectItem>
            <SelectItem value="signing_key">Signing Key</SelectItem>
            <SelectItem value="user">User</SelectItem>
            <SelectItem value="api_key">API Key</SelectItem>
            <SelectItem value="webhook">Webhook</SelectItem>
            <SelectItem value="addon">Addon</SelectItem>
            <SelectItem value="seat">Seat</SelectItem>
          </SelectContent>
        </Select>
        <Input
          placeholder={t("audit.filterEntityId")}
          value={entityIdFilter}
          onChange={(e) => {
            setEntityIdFilter(e.target.value)
            setPage(0)
          }}
          className="w-full sm:w-64"
        />
        {/* Searched on the server: an install past the first page of
            products could otherwise never filter the log by the rest
            of them. The type still shows beside each name. */}
        <ProductSelect
          value={productFilter}
          onChange={(v) => {
            setProductFilter(v)
            setPage(0)
          }}
          allLabel={t("audit.allProducts")}
          placeholder={t("audit.filterProduct")}
          className="w-full sm:w-64"
          withType
        />
      </div>

      <Card>
        <CardContent className="pt-6">
          {isLoading ? (
            <div className="h-64 animate-pulse bg-muted rounded-lg" />
          ) : (
            <>
              <DataTable>
                <DataTableHeader>
                  <DataTableRow>
                    <DataTableHead>{t("common.created")}</DataTableHead>
                    <DataTableHead>{t("audit.entity")}</DataTableHead>
                    <DataTableHead>{t("audit.action")}</DataTableHead>
                    <DataTableHead>{t("audit.actor")}</DataTableHead>
                    <DataTableHead>{t("audit.changes")}</DataTableHead>
                    <DataTableHead className="w-16 text-right">{t("common.actions") || "Actions"}</DataTableHead>
                  </DataTableRow>
                </DataTableHeader>
                <DataTableBody>
                  {logs.length === 0 && <DataTableEmpty colSpan={6} message={t("audit.empty")} />}
                  {logs.map((log) => (
                    <DataTableRow key={log.id}>
                      <DataTableCell className="text-xs text-muted-foreground whitespace-nowrap">
                        {formatDate(log.created_at)}
                      </DataTableCell>
                      <DataTableCell>
                        <span className="font-medium">{log.entity}</span>
                        <span className="text-muted-foreground text-xs ml-1 break-all">
                          #{log.entity_id.substring(0, 8)}
                        </span>
                      </DataTableCell>
                      <DataTableCell>
                        <Badge className={actionColor(log.action)}>{log.action}</Badge>
                      </DataTableCell>
                      <DataTableCell className="text-muted-foreground text-xs">
                        {log.actor_type || "-"}
                        {log.ip_address && <span className="ml-1">({log.ip_address})</span>}
                      </DataTableCell>
                      <DataTableCell className="text-xs text-muted-foreground max-w-xs truncate">
                        {log.changes && Object.keys(log.changes).length > 0 ? JSON.stringify(log.changes) : "-"}
                      </DataTableCell>
                      <DataTableCell className="text-right">
                        <Button
                          variant="ghost"
                          size="icon"
                          onClick={() => setSelectedLog(log)}
                          title="Inspect audit log details"
                          aria-label="Inspect audit log details"
                        >
                          <Eye className="h-4 w-4" />
                        </Button>
                      </DataTableCell>
                    </DataTableRow>
                  ))}
                </DataTableBody>
              </DataTable>
              {total > 0 && (
                <DataTablePagination
                  page={page}
                  totalPages={totalPages}
                  total={total}
                  pageSize={limit}
                  onPageChange={setPage}
                />
              )}
            </>
          )}
        </CardContent>
      </Card>

      <Dialog open={!!selectedLog} onOpenChange={(open) => !open && setSelectedLog(null)}>
        <DialogContent className="max-w-2xl">
          <DialogHeader>
            <DialogTitle className="flex items-center gap-2">
              <span>Audit Event Details</span>
              {selectedLog && (
                <Badge className={actionColor(selectedLog.action)}>{selectedLog.action}</Badge>
              )}
            </DialogTitle>
            <DialogDescription>
              Recorded audit trail record and event payload for security compliance.
            </DialogDescription>
          </DialogHeader>
          {selectedLog && (
            <DialogBody className="space-y-4 py-2">
              <div className="grid grid-cols-2 gap-3 text-sm">
                <div>
                  <span className="text-xs text-muted-foreground block">Event ID</span>
                  <div className="flex items-center gap-1.5 mt-0.5">
                    <span className="font-mono text-xs">{selectedLog.id}</span>
                    <Button
                      variant="ghost"
                      size="icon"
                      className="h-6 w-6"
                      onClick={() => copyToClipboard(selectedLog.id, "id")}
                      aria-label="Copy event ID"
                    >
                      {copiedField === "id" ? <Check className="h-3 w-3 text-emerald-600" /> : <Copy className="h-3 w-3" />}
                    </Button>
                  </div>
                </div>
                <div>
                  <span className="text-xs text-muted-foreground block">Recorded Timestamp</span>
                  <span className="text-xs">{formatDate(selectedLog.created_at)}</span>
                </div>
                <div>
                  <span className="text-xs text-muted-foreground block">Entity Type & Target ID</span>
                  <div className="flex items-center gap-1.5 mt-0.5">
                    <span className="font-mono text-xs">
                      {selectedLog.entity}: {selectedLog.entity_id}
                    </span>
                    <Button
                      variant="ghost"
                      size="icon"
                      className="h-6 w-6"
                      onClick={() => copyToClipboard(selectedLog.entity_id, "entity_id")}
                      aria-label="Copy entity ID"
                    >
                      {copiedField === "entity_id" ? <Check className="h-3 w-3 text-emerald-600" /> : <Copy className="h-3 w-3" />}
                    </Button>
                  </div>
                </div>
                <div>
                  <span className="text-xs text-muted-foreground block">Actor Information</span>
                  <span className="text-xs font-mono">
                    {selectedLog.actor_type || "system"}
                    {selectedLog.actor_id ? ` (${selectedLog.actor_id})` : ""}
                  </span>
                </div>
                {selectedLog.ip_address && (
                  <div>
                    <span className="text-xs text-muted-foreground block">Origin IP Address</span>
                    <span className="text-xs font-mono">{selectedLog.ip_address}</span>
                  </div>
                )}
              </div>

              <div>
                <div className="flex items-center justify-between mb-1.5">
                  <span className="text-xs font-medium text-muted-foreground">Changes / Event Payload</span>
                  {selectedLog.changes && (
                    <Button
                      variant="outline"
                      size="sm"
                      className="h-7 text-xs"
                      onClick={() => copyToClipboard(JSON.stringify(selectedLog.changes, null, 2), "payload")}
                    >
                      {copiedField === "payload" ? (
                        <>
                          <Check className="h-3 w-3 mr-1 text-emerald-600" />
                          Copied
                        </>
                      ) : (
                        <>
                          <Copy className="h-3 w-3 mr-1" />
                          Copy JSON
                        </>
                      )}
                    </Button>
                  )}
                </div>
                {selectedLog.changes && Object.keys(selectedLog.changes).length > 0 ? (
                  <pre className="bg-muted p-3 rounded-md text-xs font-mono overflow-x-auto max-h-60 border">
                    {JSON.stringify(selectedLog.changes, null, 2)}
                  </pre>
                ) : (
                  <p className="text-xs text-muted-foreground italic">No state changes recorded for this event.</p>
                )}
              </div>
            </DialogBody>
          )}
          <DialogFooter>
            <Button variant="outline" onClick={() => setSelectedLog(null)}>
              {t("common.close")}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  )
}
