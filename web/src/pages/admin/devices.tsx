import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import {
  Copy,
  Cpu,
  Laptop,
  Pause,
  RefreshCw,
  RotateCcw,
  Search,
  Smartphone,
  Trash2,
} from "lucide-react"
import { useMemo, useState } from "react"
import { Link } from "react-router-dom"
import { ProductSelect } from "@/components/product-select"
import { showToast } from "@/components/toast"
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import {
  DataTable,
  DataTableBody,
  DataTableCell,
  DataTableEmpty,
  DataTableHead,
  DataTableHeader,
  DataTablePagination,
  DataTableRow,
  useServerPagination,
} from "@/components/ui/data-table"
import { Input } from "@/components/ui/input"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { useI18n } from "@/i18n"
import { admin, type Activation } from "@/lib/api"
import { formatDate } from "@/lib/utils"

export default function DevicesPage() {
  const { t } = useI18n()
  const qc = useQueryClient()

  const [productFilter, setProductFilter] = useState("")
  const [statusFilter, setStatusFilter] = useState<string>("all")
  const [schemeFilter, setSchemeFilter] = useState<string>("all")
  const [search, setSearch] = useState("")

  const [confirmDeactivation, setConfirmDeactivation] = useState<{ id: string; label: string } | null>(null)
  const [confirmReactivation, setConfirmReactivation] = useState<{ id: string; label: string } | null>(null)
  const [confirmDelete, setConfirmDelete] = useState<{ id: string; label: string } | null>(null)

  // Fetch licenses to collect activations and plan capacities
  const { data: licensesData, isLoading: loadingLicenses, refetch } = useQuery({
    queryKey: ["admin", "devices-licenses", productFilter],
    queryFn: () => admin.listLicenses({ product_id: productFilter || undefined, limit: 100 }),
  })

  const licenses = licensesData?.licenses || []

  // Collect activations with parent license reference
  const allActivationsWithLicense = useMemo(() => {
    const list: Array<Activation & { license_email: string; product_id: string; product_name?: string }> = []
    for (const lic of licenses) {
      if (lic.activations && lic.activations.length > 0) {
        for (const act of lic.activations) {
          list.push({
            ...act,
            license_email: lic.email,
            product_id: lic.product_id,
            product_name: lic.product?.name,
          })
        }
      }
    }
    return list
  }, [licenses])

  // Filter activations according to UI controls
  const filteredActivations = useMemo(() => {
    return allActivationsWithLicense.filter((act) => {
      // Status filter
      const isActive = act.status === "active" || (!act.status && act.status !== "deactivated")
      if (statusFilter === "active" && !isActive) return false
      if (statusFilter === "deactivated" && isActive) return false

      // Scheme filter
      const scheme = act.scheme || "legacy"
      if (schemeFilter !== "all" && scheme !== schemeFilter) return false

      // Search query (identifier, label, IP address, license email)
      if (search.trim()) {
        const query = search.trim().toLowerCase()
        const matchesIdentifier = act.identifier?.toLowerCase().includes(query)
        const matchesLabel = act.label?.toLowerCase().includes(query)
        const matchesIP = act.ip_address?.toLowerCase().includes(query)
        const matchesEmail = act.license_email?.toLowerCase().includes(query)
        if (!matchesIdentifier && !matchesLabel && !matchesIP && !matchesEmail) {
          return false
        }
      }

      return true
    })
  }, [allActivationsWithLicense, statusFilter, schemeFilter, search])

  // Pagination on client-filtered set
  const pg = useServerPagination(20, [statusFilter, schemeFilter, search, productFilter])
  const total = filteredActivations.length
  const totalPages = Math.ceil(total / pg.pageSize)
  const paginatedActivations = filteredActivations.slice(pg.page * pg.pageSize, (pg.page + 1) * pg.pageSize)

  // Metrics
  const metrics = useMemo(() => {
    let active = 0
    let deactivated = 0
    const schemes: Record<string, number> = {}

    for (const act of allActivationsWithLicense) {
      const isActive = act.status === "active" || (!act.status && act.status !== "deactivated")
      if (isActive) {
        active++
      } else {
        deactivated++
      }
      const s = act.scheme || "legacy"
      schemes[s] = (schemes[s] || 0) + 1
    }

    return {
      total: allActivationsWithLicense.length,
      active,
      deactivated,
      schemes,
    }
  }, [allActivationsWithLicense])

  // Mutations
  const deactivateMut = useMutation({
    mutationFn: (actId: string) => admin.deactivateActivation(actId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["admin"] })
      setConfirmDeactivation(null)
      showToast(t("licenses.deactivateSuccess"), "success")
    },
    onError: (e: Error) => showToast(e.message, "error"),
  })

  const reactivateMut = useMutation({
    mutationFn: (actId: string) => admin.reactivateActivation(actId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["admin"] })
      setConfirmReactivation(null)
      showToast(t("licenses.reactivateSuccess"), "success")
    },
    onError: (e: Error) => showToast(e.message, "error"),
  })

  const deleteActMut = useMutation({
    mutationFn: (actId: string) => admin.deleteActivation(actId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["admin"] })
      setConfirmDelete(null)
      showToast("Activation record deleted", "success")
    },
    onError: (e: Error) => showToast(e.message, "error"),
  })

  const copyText = (val: string) => {
    navigator.clipboard.writeText(val)
    showToast("Copied to clipboard", "success")
  }

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="text-2xl font-bold tracking-tight">{t("devices.title")}</h1>
          <p className="text-muted-foreground">{t("devices.subtitle")}</p>
        </div>
        <Button variant="outline" size="sm" onClick={() => refetch()}>
          <RefreshCw className="h-4 w-4 mr-2" />
          {t("common.refresh")}
        </Button>
      </div>

      {/* Summary Cards */}
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <Card>
          <CardHeader className="flex flex-row items-center justify-between pb-2 space-y-0">
            <CardTitle className="text-sm font-medium">{t("devices.totalTracked")}</CardTitle>
            <Laptop className="h-4 w-4 text-muted-foreground" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold">{metrics.total}</div>
          </CardContent>
        </Card>
        <Card>
          <CardHeader className="flex flex-row items-center justify-between pb-2 space-y-0">
            <CardTitle className="text-sm font-medium">{t("devices.activeSlots")}</CardTitle>
            <Smartphone className="h-4 w-4 text-emerald-600" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold text-emerald-600">{metrics.active}</div>
          </CardContent>
        </Card>
        <Card>
          <CardHeader className="flex flex-row items-center justify-between pb-2 space-y-0">
            <CardTitle className="text-sm font-medium">{t("devices.deactivated")}</CardTitle>
            <Pause className="h-4 w-4 text-amber-600" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold text-amber-600">{metrics.deactivated}</div>
          </CardContent>
        </Card>
        <Card>
          <CardHeader className="flex flex-row items-center justify-between pb-2 space-y-0">
            <CardTitle className="text-sm font-medium">{t("devices.schemes")}</CardTitle>
            <Cpu className="h-4 w-4 text-muted-foreground" />
          </CardHeader>
          <CardContent>
            <div className="flex flex-wrap gap-1 mt-1">
              {Object.entries(metrics.schemes).map(([scheme, count]) => (
                <Badge key={scheme} variant="outline" className="text-[11px] font-mono">
                  {scheme}: {count}
                </Badge>
              ))}
              {Object.keys(metrics.schemes).length === 0 && (
                <span className="text-xs text-muted-foreground">-</span>
              )}
            </div>
          </CardContent>
        </Card>
      </div>

      {/* Filter and Search Bar */}
      <div className="flex flex-wrap items-center gap-3">
        <div className="relative w-full sm:w-auto sm:flex-1 sm:max-w-sm">
          <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-muted-foreground" />
          <Input
            placeholder={t("devices.searchPlaceholder")}
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            className="pl-9"
          />
        </div>

        <ProductSelect
          value={productFilter}
          onChange={setProductFilter}
          allLabel={t("filter.allProducts")}
          className="w-full sm:w-48"
        />

        <Select value={statusFilter} onValueChange={setStatusFilter}>
          <SelectTrigger className="w-full sm:w-40">
            <SelectValue placeholder={t("devices.filterStatus")} />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="all">{t("devices.allStatuses")}</SelectItem>
            <SelectItem value="active">{t("licenses.activeStatus")}</SelectItem>
            <SelectItem value="deactivated">{t("licenses.deactivated")}</SelectItem>
          </SelectContent>
        </Select>

        <Select value={schemeFilter} onValueChange={setSchemeFilter}>
          <SelectTrigger className="w-full sm:w-40">
            <SelectValue placeholder={t("devices.filterScheme")} />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="all">{t("devices.allSchemes")}</SelectItem>
            <SelectItem value="kgid-v1">kgid-v1</SelectItem>
            <SelectItem value="sha256-v1">sha256-v1</SelectItem>
            <SelectItem value="uuid">uuid</SelectItem>
            <SelectItem value="legacy">legacy</SelectItem>
          </SelectContent>
        </Select>
      </div>

      {/* Devices Data Table */}
      <Card>
        <CardContent className="p-0">
          <DataTable>
            <DataTableHeader>
              <DataTableRow>
                <DataTableHead>{t("devices.identifier")}</DataTableHead>
                <DataTableHead>{t("devices.hardwareScheme")}</DataTableHead>
                <DataTableHead>{t("devices.machineLabel")}</DataTableHead>
                <DataTableHead>{t("devices.license")}</DataTableHead>
                <DataTableHead>{t("common.status")}</DataTableHead>
                <DataTableHead>{t("devices.lastActive")}</DataTableHead>
                <DataTableHead className="w-24 text-right">{t("common.actions")}</DataTableHead>
              </DataTableRow>
            </DataTableHeader>
            <DataTableBody>
              {loadingLicenses ? (
                <DataTableRow>
                  <DataTableCell colSpan={7} className="h-24 text-center">
                    <RefreshCw className="h-5 w-5 animate-spin mx-auto text-muted-foreground" />
                  </DataTableCell>
                </DataTableRow>
              ) : paginatedActivations.length === 0 ? (
                <DataTableEmpty colSpan={7} message={t("devices.empty")} />
              ) : (
                paginatedActivations.map((act) => {
                  const isActive = act.status === "active" || (!act.status && act.status !== "deactivated")
                  return (
                    <DataTableRow key={act.id}>
                      <DataTableCell>
                        <div className="flex items-center gap-1.5">
                          <code
                            className="block max-w-[22ch] truncate text-xs font-mono select-all"
                            title={act.identifier}
                          >
                            {act.identifier}
                          </code>
                          <Button
                            variant="ghost"
                            size="icon"
                            className="h-6 w-6 text-muted-foreground hover:text-foreground"
                            onClick={() => copyText(act.identifier)}
                            title="Copy identifier"
                          >
                            <Copy className="h-3 w-3" />
                          </Button>
                        </div>
                      </DataTableCell>
                      <DataTableCell>
                        <div className="flex flex-col gap-0.5 items-start">
                          <Badge variant="outline" className="text-[10px] font-mono">
                            {act.scheme || "legacy"}
                          </Badge>
                          <span className="text-[10px] text-muted-foreground">{act.identifier_type}</span>
                        </div>
                      </DataTableCell>
                      <DataTableCell>
                        <div className="text-xs">
                          <p className="font-medium">{act.label || "-"}</p>
                          {act.ip_address && (
                            <p className="text-[11px] text-muted-foreground font-mono">{act.ip_address}</p>
                          )}
                        </div>
                      </DataTableCell>
                      <DataTableCell>
                        <div className="text-xs">
                          <Link
                            to="/admin/licenses"
                            className="font-medium text-primary hover:underline truncate block max-w-[20ch]"
                            title={act.license_email}
                          >
                            {act.license_email}
                          </Link>
                          {act.product_name && (
                            <span className="text-[10px] text-muted-foreground">{act.product_name}</span>
                          )}
                        </div>
                      </DataTableCell>
                      <DataTableCell>
                        {isActive ? (
                          <Badge className="bg-emerald-100 text-emerald-800 text-[10px]">
                            {t("licenses.activeStatus")}
                          </Badge>
                        ) : (
                          <div className="flex flex-col gap-0.5 items-start">
                            <Badge variant="secondary" className="text-[10px] text-muted-foreground">
                              {t("licenses.deactivated")}
                            </Badge>
                            {act.deactivated_reason && (
                              <span className="text-[10px] text-muted-foreground italic">
                                ({act.deactivated_reason})
                              </span>
                            )}
                          </div>
                        )}
                      </DataTableCell>
                      <DataTableCell className="text-muted-foreground text-xs">
                        <div>
                          <p title="Last verified">{formatDate(act.last_verified)}</p>
                          {act.deactivated_at && (
                            <p className="text-[10px] text-muted-foreground">
                              Off: {formatDate(act.deactivated_at)}
                            </p>
                          )}
                        </div>
                      </DataTableCell>
                      <DataTableCell>
                        <div className="flex items-center gap-1 justify-end">
                          {isActive ? (
                            <Button
                              variant="ghost"
                              size="icon"
                              className="h-7 w-7 text-amber-600 hover:text-amber-700"
                              title={t("licenses.deactivate")}
                              onClick={() =>
                                setConfirmDeactivation({ id: act.id, label: act.label || act.identifier })
                              }
                              disabled={deactivateMut.isPending && deactivateMut.variables === act.id}
                            >
                              <Pause className="h-3.5 w-3.5" />
                            </Button>
                          ) : (
                            <Button
                              variant="ghost"
                              size="icon"
                              className="h-7 w-7 text-emerald-600 hover:text-emerald-700"
                              title={t("licenses.reactivate")}
                              onClick={() =>
                                setConfirmReactivation({ id: act.id, label: act.label || act.identifier })
                              }
                              disabled={reactivateMut.isPending && reactivateMut.variables === act.id}
                            >
                              <RotateCcw className="h-3.5 w-3.5" />
                            </Button>
                          )}
                          <Button
                            variant="ghost"
                            size="icon"
                            className="h-7 w-7 text-destructive"
                            title={t("licenses.deleteActivation")}
                            onClick={() =>
                              setConfirmDelete({ id: act.id, label: act.label || act.identifier })
                            }
                            disabled={deleteActMut.isPending && deleteActMut.variables === act.id}
                          >
                            <Trash2 className="h-3 w-3" />
                          </Button>
                        </div>
                      </DataTableCell>
                    </DataTableRow>
                  )
                })
              )}
            </DataTableBody>
          </DataTable>
        </CardContent>
      </Card>

      {total > pg.pageSize && (
        <DataTablePagination
          page={pg.page}
          totalPages={totalPages}
          total={total}
          pageSize={pg.pageSize}
          onPageChange={pg.setPage}
          onPageSizeChange={pg.setPageSize}
        />
      )}

      {/* Confirmation Dialogs */}
      <AlertDialog open={!!confirmDeactivation} onOpenChange={() => setConfirmDeactivation(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>{t("devices.deactivateTitle")}</AlertDialogTitle>
            <AlertDialogDescription>
              {t("devices.deactivateDesc")} (Device: {confirmDeactivation?.label})
            </AlertDialogDescription>
          </AlertDialogHeader>
          <div className="flex justify-end gap-2 mt-4">
            <AlertDialogCancel>{t("common.cancel")}</AlertDialogCancel>
            <AlertDialogAction
              className="bg-amber-600 text-white hover:bg-amber-700"
              onClick={() => confirmDeactivation && deactivateMut.mutate(confirmDeactivation.id)}
              disabled={deactivateMut.isPending}
            >
              {deactivateMut.isPending ? t("common.loading") : t("licenses.deactivate")}
            </AlertDialogAction>
          </div>
        </AlertDialogContent>
      </AlertDialog>

      <AlertDialog open={!!confirmReactivation} onOpenChange={() => setConfirmReactivation(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>{t("devices.reactivateTitle")}</AlertDialogTitle>
            <AlertDialogDescription>
              {t("devices.reactivateDesc")} (Device: {confirmReactivation?.label})
            </AlertDialogDescription>
          </AlertDialogHeader>
          <div className="flex justify-end gap-2 mt-4">
            <AlertDialogCancel>{t("common.cancel")}</AlertDialogCancel>
            <AlertDialogAction
              className="bg-emerald-600 text-white hover:bg-emerald-700"
              onClick={() => confirmReactivation && reactivateMut.mutate(confirmReactivation.id)}
              disabled={reactivateMut.isPending}
            >
              {reactivateMut.isPending ? t("common.loading") : t("licenses.reactivate")}
            </AlertDialogAction>
          </div>
        </AlertDialogContent>
      </AlertDialog>

      <AlertDialog open={!!confirmDelete} onOpenChange={() => setConfirmDelete(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>{t("devices.deleteTitle")}</AlertDialogTitle>
            <AlertDialogDescription>
              {t("devices.deleteDesc")} (Device: {confirmDelete?.label})
            </AlertDialogDescription>
          </AlertDialogHeader>
          <div className="flex justify-end gap-2 mt-4">
            <AlertDialogCancel>{t("common.cancel")}</AlertDialogCancel>
            <AlertDialogAction
              className="bg-destructive text-white hover:bg-destructive/90"
              onClick={() => confirmDelete && deleteActMut.mutate(confirmDelete.id)}
              disabled={deleteActMut.isPending}
            >
              {deleteActMut.isPending ? t("common.loading") : t("common.delete")}
            </AlertDialogAction>
          </div>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  )
}
