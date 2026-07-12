// kiriha.nephilim.jp のランディングページ配信 Worker。
//
// 同じホスト名の R2 カスタムドメインは Velopack の更新ファイルを配信する。
// トップページだけを Worker から返し、Setup.exe や nupkg、リリース情報は
// R2 へそのまま委譲して Range / キャッシュなどの配信挙動を維持する。
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

    // 更新配信パスには手を加えず R2 のレスポンスを返す。
    return fetch(request);
  },
};
