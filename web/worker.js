// kiriha.nephilim.jp のランディングページ配信 + ライセンス発行 Worker。
//
// トップページは Worker、更新ファイルは R2 バケットから返す。
// ライセンスは署名付きキー方式（外部ライセンス基盤に依存しない）:
//   /buy             → Stripe Payment Link へリダイレクト
//   /license/issue   → 決済成功ページ。Checkout Session を検証してキーを署名発行・表示
//   /license/check   → 失効リスト照会（アプリの起動時チェック用、KV: REVOKED）
//   /license/webhook → Stripe webhook（charge.refunded で失効登録）
// 必要な secret: STRIPE_SECRET_KEY / STRIPE_WEBHOOK_SECRET / LICENSE_SIGNING_KEY（PKCS#8 base64, ECDSA P-256）
import landingHtml from "./index.html";
import tokushohoHtml from "./tokushoho.html";
import heroAppPng from "./hero-app.png";

export default {
  async fetch(request, env) {
    const { pathname, searchParams } = new URL(request.url);

    // ===== ライセンス =====

    if (pathname === "/buy") {
      return Response.redirect(env.PAYMENT_LINK_URL, 302);
    }

    if (pathname === "/license/check") {
      const id = searchParams.get("id");
      if (!id) return jsonResponse({ error: "id が必要です" }, 400);
      const revoked = await env.REVOKED.get(`revoked:${id}`);
      return jsonResponse({ valid: revoked === null }, 200, { "cache-control": "no-store" });
    }

    if (pathname === "/license/issue") {
      return issueLicense(searchParams.get("session_id"), env);
    }

    if (pathname === "/license/webhook" && request.method === "POST") {
      return handleStripeWebhook(request, env);
    }

    if (pathname === "/" || pathname === "/index.html") {
      return new Response(landingHtml, {
        headers: {
          "content-type": "text/html; charset=utf-8",
          "cache-control": "public, max-age=300",
        },
      });
    }

    // ヒーローのアプリスクリーンショット（Worker バンドル同梱）
    if (pathname === "/hero-app.png") {
      return new Response(heroAppPng, {
        headers: {
          "content-type": "image/png",
          "cache-control": "public, max-age=86400",
        },
      });
    }

    // 特定商取引法に基づく表記
    if (pathname === "/tokushoho" || pathname === "/tokushoho.html") {
      return new Response(tokushohoHtml, {
        headers: {
          "content-type": "text/html; charset=utf-8",
          "cache-control": "public, max-age=300",
        },
      });
    }

    if (request.method !== "GET" && request.method !== "HEAD") {
      return new Response("許可されていないメソッドです。", {
        status: 405,
        headers: { allow: "GET, HEAD" },
      });
    }

    let key;
    try {
      key = decodeURIComponent(pathname.slice(1));
    } catch {
      return new Response("不正なパスです。", { status: 400 });
    }

    if (request.method === "HEAD") {
      const metadata = await env.UPDATES.head(key);
      if (metadata === null) return notFound();
      const headers = buildHeaders(metadata, key);
      headers.set("content-length", String(metadata.size));
      return new Response(null, { headers });
    }

    const requestedRange = parseRangeHeader(request.headers.get("range"));
    if (requestedRange === false) return new Response(null, { status: 416 });
    let object;
    try {
      object = await env.UPDATES.get(key, requestedRange ? { range: requestedRange } : undefined);
    } catch (error) {
      if (!requestedRange) {
        // R2 の一時的な不調を事後調査できるよう記録した上で、既定のエラーページではなく 500 を返す
        console.error(`R2 の取得に失敗: key=${key}`, error);
        return new Response("一時的なエラーが発生しました。しばらくしてから再試行してください。", { status: 500 });
      }
      const metadata = await env.UPDATES.head(key);
      if (metadata === null) return notFound();
      const headers = buildHeaders(metadata, key);
      headers.set("content-range", `bytes */${metadata.size}`);
      return new Response(null, { status: 416, headers });
    }
    if (object === null || object.body === undefined) {
      return notFound();
    }

    const headers = buildHeaders(object, key);
    if (requestedRange) {
      const returned = object.range;
      if (returned === undefined) return new Response(null, { status: 416, headers });
      const start = returned.offset;
      const end = start + returned.length - 1;
      headers.set("content-range", `bytes ${start}-${end}/${object.size}`);
      headers.set("content-length", String(returned.length));
      return new Response(object.body, { status: 206, headers });
    }

    headers.set("content-length", String(object.size));
    return new Response(object.body, { headers });
  },
};

// ===== ライセンス発行・失効 =====

function jsonResponse(obj, status = 200, extraHeaders = {}) {
  return new Response(JSON.stringify(obj), {
    status,
    headers: { "content-type": "application/json; charset=utf-8", ...extraHeaders },
  });
}

/** 決済成功ページ: Checkout Session を Stripe API で検証し、署名済みキーを発行して表示する。 */
async function issueLicense(sessionId, env) {
  if (!sessionId || !/^cs_[a-zA-Z0-9_]+$/.test(sessionId)) {
    return new Response("不正なリクエストです。", { status: 400 });
  }

  const res = await fetch(`https://api.stripe.com/v1/checkout/sessions/${sessionId}`, {
    headers: { authorization: `Bearer ${env.STRIPE_SECRET_KEY}` },
  });
  if (!res.ok) {
    console.error(`Stripe セッション取得に失敗: ${res.status}`);
    return new Response("決済情報を確認できませんでした。時間をおいて再読み込みしてください。", { status: 502 });
  }

  const session = await res.json();
  if (session.payment_status !== "paid") {
    return new Response("お支払いが完了していません。決済完了後にもう一度開いてください。", { status: 402 });
  }

  const email = (session.customer_details?.email ?? session.customer_email ?? "").toLowerCase();
  const purchaseId = session.payment_intent ?? session.id;
  const key = await signLicenseKey({ e: email, p: purchaseId, d: new Date(session.created * 1000).toISOString() }, env);

  return new Response(licensePageHtml(key, email), {
    headers: { "content-type": "text/html; charset=utf-8", "cache-control": "no-store" },
  });
}

/** payload を ECDSA P-256 / SHA-256 で署名し KIRIHA-<payload>-<sig> 形式のキーにする。 */
async function signLicenseKey(payload, env) {
  const der = Uint8Array.from(atob(env.LICENSE_SIGNING_KEY), (c) => c.charCodeAt(0));
  const privateKey = await crypto.subtle.importKey(
    "pkcs8", der, { name: "ECDSA", namedCurve: "P-256" }, false, ["sign"]);
  const payloadBytes = new TextEncoder().encode(JSON.stringify(payload));
  const signature = await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, privateKey, payloadBytes);
  // base64url 自体が '-' を含むため、payload と署名の区切りは '.'（JWT 風）
  return `KIRIHA-${base64Url(payloadBytes)}.${base64Url(new Uint8Array(signature))}`;
}

function base64Url(bytes) {
  let binary = "";
  for (const b of bytes) binary += String.fromCharCode(b);
  return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/, "");
}

/** Stripe webhook: 署名を検証し、返金でライセンスを失効リスト（KV）へ登録する。 */
async function handleStripeWebhook(request, env) {
  const body = await request.text();
  if (!(await verifyStripeSignature(body, request.headers.get("stripe-signature"), env.STRIPE_WEBHOOK_SECRET))) {
    return new Response("署名が不正です。", { status: 400 });
  }

  const event = JSON.parse(body);
  if (event.type === "charge.refunded") {
    const paymentIntent = event.data?.object?.payment_intent;
    if (paymentIntent) {
      await env.REVOKED.put(`revoked:${paymentIntent}`, new Date().toISOString());
      console.log(`ライセンス失効を登録: ${paymentIntent}`);
    }
  }

  return jsonResponse({ received: true });
}

/** Stripe-Signature ヘッダー（t=...,v1=...）の HMAC-SHA256 検証。 */
async function verifyStripeSignature(body, header, secret) {
  if (!header || !secret) return false;
  const parts = Object.fromEntries(header.split(",").map((p) => p.split("=")));
  if (!parts.t || !parts.v1) return false;
  // リプレイ対策: 5 分より古い署名は拒否する
  if (Math.abs(Date.now() / 1000 - Number(parts.t)) > 300) return false;

  const key = await crypto.subtle.importKey(
    "raw", new TextEncoder().encode(secret), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const mac = await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(`${parts.t}.${body}`));
  const expected = [...new Uint8Array(mac)].map((b) => b.toString(16).padStart(2, "0")).join("");
  if (expected.length !== parts.v1.length) return false;
  let diff = 0;
  for (let i = 0; i < expected.length; i++) diff |= expected.charCodeAt(i) ^ parts.v1.charCodeAt(i);
  return diff === 0;
}

/** 決済完了ページ（キー表示）。 */
function licensePageHtml(key, email) {
  const esc = (s) => s.replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
  return `<!DOCTYPE html>
<html lang="ja">
<head>
<meta charset="UTF-8" />
<meta name="viewport" content="width=device-width, initial-scale=1.0" />
<meta name="robots" content="noindex" />
<title>ご購入ありがとうございます — Kiriha</title>
<link rel="preconnect" href="https://fonts.googleapis.com" />
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin />
<link href="https://fonts.googleapis.com/css2?family=IBM+Plex+Sans+JP:wght@400;500;600;700&display=swap" rel="stylesheet" />
<style>
  body { margin: 0; font-family: "IBM Plex Sans JP", system-ui, sans-serif; background: #fafafa; color: #11181c; line-height: 1.7; }
  .wrap { max-width: 640px; margin: 0 auto; padding: 64px 24px; }
  .card { background: #fff; border: 1px solid #e4e4e7; border-radius: 22px; padding: 36px; box-shadow: 0 12px 34px rgba(0,0,0,.08); }
  h1 { font-size: 22px; margin: 0 0 6px; }
  p { color: #52525b; font-size: 14px; margin: 10px 0; }
  .key { display: block; word-break: break-all; background: #f4f4f5; border: 1px solid #e4e4e7; border-radius: 12px; padding: 16px; font-family: Consolas, monospace; font-size: 13px; margin: 18px 0 10px; user-select: all; }
  button { padding: 11px 22px; border-radius: 999px; border: 0; background: #006fee; color: #fff; font-weight: 700; font-size: 14px; cursor: pointer; font-family: inherit; }
  button:hover { opacity: .9; }
  .note { font-size: 12.5px; color: #a1a1aa; }
</style>
</head>
<body>
<div class="wrap">
  <div class="card">
    <h1>ご購入ありがとうございます 🎉</h1>
    <p>${esc(email)} 宛のご購入を確認しました。以下があなたのライセンスキーです。</p>
    <code class="key" id="key">${esc(key)}</code>
    <button onclick="navigator.clipboard.writeText(document.getElementById('key').textContent).then(() => this.textContent = 'コピーしました ✓')">キーをコピー</button>
    <p>Kiriha の <b>設定 &gt; 更新と情報 &gt; ライセンス</b> にこのキーを貼り付けて「認証する」を押してください。</p>
    <p class="note">このページをブックマークするか、キーを安全な場所に保存してください。キーを紛失した場合は、購入時のメールアドレスを添えてお問い合わせいただければ再発行します。</p>
  </div>
</div>
</body>
</html>`;
}

function notFound() {
  return new Response("ページが見つかりません。", {
    status: 404,
    headers: { "cache-control": "no-store" },
  });
}

function buildHeaders(object, key) {
  const headers = new Headers();
  object.writeHttpMetadata(headers);
  headers.set("etag", object.httpEtag);
  headers.set("last-modified", object.uploaded.toUTCString());
  headers.set("accept-ranges", "bytes");

  if (!headers.has("content-type")) {
    headers.set("content-type", contentTypeFor(key));
  }
  if (!headers.has("cache-control")) {
    headers.set(
      "cache-control",
      key.endsWith(".nupkg")
        ? "public, max-age=31536000, immutable"
        : key.startsWith("releases.")
          ? "public, max-age=60, must-revalidate"
          : "public, max-age=300",
    );
  }
  return headers;
}

function contentTypeFor(key) {
  if (key.endsWith(".json")) return "application/json; charset=utf-8";
  if (key.endsWith(".exe")) return "application/vnd.microsoft.portable-executable";
  if (key.endsWith(".zip") || key.endsWith(".nupkg")) return "application/zip";
  return "application/octet-stream";
}

function parseRangeHeader(value) {
  if (value === null) return null;
  const match = /^bytes=(\d*)-(\d*)$/.exec(value.trim());
  if (match === null || (!match[1] && !match[2])) return false;

  if (!match[1]) {
    const suffix = Number(match[2]);
    if (!Number.isSafeInteger(suffix) || suffix <= 0) return false;
    return { suffix };
  }

  const start = Number(match[1]);
  const requestedEnd = match[2] ? Number(match[2]) : undefined;
  if (
    !Number.isSafeInteger(start) ||
    start < 0 ||
    (requestedEnd !== undefined &&
      (!Number.isSafeInteger(requestedEnd) || requestedEnd < start))
  ) {
    return false;
  }

  return requestedEnd === undefined
    ? { offset: start }
    : { offset: start, length: requestedEnd - start + 1 };
}
