import type { OfflineTokenPayload } from "@/lib/api"

export interface ParsedOfflineToken {
  raw: string
  versionPrefix?: string
  format: "v1-prefixed" | "legacy" | "invalid"
  header?: {
    ver?: string
    kid?: string
  }
  payload?: OfflineTokenPayload
  signatureHex?: string
  signatureBase64?: string
  payloadBase64?: string
  error?: string
  isExpired: boolean
  isTermExpired: boolean
  offlineExpiresAt?: Date
  termExpiresAt?: Date
  issuedAt?: Date
}

function base64Decode(str: string): string {
  // Convert URL-safe base64 to standard base64 and pad
  let b64 = str.replace(/-/g, "+").replace(/_/g, "/")
  while (b64.length % 4) {
    b64 += "="
  }
  try {
    return decodeURIComponent(
      atob(b64)
        .split("")
        .map((c) => "%" + ("00" + c.charCodeAt(0).toString(16)).slice(-2))
        .join(""),
    )
  } catch {
    return atob(b64)
  }
}

function base64ToHex(b64: string): string {
  try {
    const raw = atob(b64.replace(/-/g, "+").replace(/_/g, "/"))
    let hex = ""
    for (let i = 0; i < raw.length; i++) {
      hex += ("00" + raw.charCodeAt(i).toString(16)).slice(-2)
    }
    return hex
  } catch {
    return ""
  }
}

/**
 * parseOfflineToken safely inspects and decodes a Keygate offline license token.
 * It extracts claims, evaluates offline validity window, checks license term expiration,
 * and formats device binding fingerprints.
 */
export function parseOfflineToken(rawToken: string): ParsedOfflineToken {
  const trimmed = rawToken.trim()
  if (!trimmed) {
    return {
      raw: "",
      format: "invalid",
      error: "Empty token provided",
      isExpired: false,
      isTermExpired: false,
    }
  }

  let payloadB64 = ""
  let signatureB64 = ""
  let format: "v1-prefixed" | "legacy" | "invalid" = "legacy"
  let versionPrefix: string | undefined

  if (trimmed.startsWith("keygate.offline.v1.")) {
    format = "v1-prefixed"
    versionPrefix = "keygate.offline.v1"
    const parts = trimmed.split(".")
    if (parts.length !== 5) {
      return {
        raw: trimmed,
        format: "invalid",
        error: "Malformed token: expected 5 dot-separated segments for keygate.offline.v1 format",
        isExpired: false,
        isTermExpired: false,
      }
    }
    payloadB64 = parts[3]
    signatureB64 = parts[4]
  } else {
    // Legacy format: <payload>.<signature>
    const parts = trimmed.split(".")
    if (parts.length !== 2) {
      return {
        raw: trimmed,
        format: "invalid",
        error: "Malformed token: expected dot-separated payload and signature",
        isExpired: false,
        isTermExpired: false,
      }
    }
    payloadB64 = parts[0]
    signatureB64 = parts[1]
  }

  let payload: OfflineTokenPayload
  try {
    const decodedJson = base64Decode(payloadB64)
    payload = JSON.parse(decodedJson) as OfflineTokenPayload
  } catch (e) {
    return {
      raw: trimmed,
      format,
      versionPrefix,
      payloadBase64: payloadB64,
      signatureBase64: signatureB64,
      error: `Failed to decode or parse token payload JSON: ${e instanceof Error ? e.message : String(e)}`,
      isExpired: false,
      isTermExpired: false,
    }
  }

  const nowSec = Math.floor(Date.now() / 1000)
  const isExpired = payload.exp > 0 && payload.exp < nowSec
  const isTermExpired = payload.vun !== undefined && payload.vun > 0 && payload.vun < nowSec

  return {
    raw: trimmed,
    format,
    versionPrefix,
    header: {
      ver: payload.ver,
      kid: payload.kid,
    },
    payload,
    payloadBase64: payloadB64,
    signatureBase64: signatureB64,
    signatureHex: base64ToHex(signatureB64),
    isExpired,
    isTermExpired,
    offlineExpiresAt: payload.exp ? new Date(payload.exp * 1000) : undefined,
    termExpiresAt: payload.vun ? new Date(payload.vun * 1000) : undefined,
    issuedAt: payload.iat ? new Date(payload.iat * 1000) : undefined,
  }
}
