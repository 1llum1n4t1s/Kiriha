// kiriha.nephilim.jp のランディングページ配信 Worker。
//
// トップページを Worker から返す。
import landingHtml from "./index.html";

export default {
  async fetch(request) {
    const { pathname } = new URL(request.url);

    if (pathname === "/" || pathname === "/index.html") {
      return new Response(landingHtml, {
        headers: {
          "content-type": "text/html; charset=utf-8",
          "cache-control": "public, max-age=300",
        },
      });
    }

    // Custom Domain 上の同じ URL を fetch すると自分自身を再呼び出しするため、
    // 配置されていないパスは明示的に 404 にする。
    return new Response("ページが見つかりません。", {
      status: 404,
      headers: {
        "content-type": "text/plain; charset=utf-8",
        "cache-control": "no-store",
      },
    });
  },
};
