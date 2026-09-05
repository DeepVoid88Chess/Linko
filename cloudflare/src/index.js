const json = (data, status = 200) => new Response(JSON.stringify(data), {
  status,
  headers: { "content-type": "application/json", "access-control-allow-origin": "*" }
});

function code() {
  return String(Math.floor(100000 + Math.random() * 900000));
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (request.method === "OPTIONS") {
      return new Response(null, { headers: {
        "access-control-allow-origin": "*",
        "access-control-allow-methods": "GET,POST,OPTIONS",
        "access-control-allow-headers": "content-type"
      }});
    }

    if (url.pathname === "/api/pair" && request.method === "POST") {
      const pairingCode = code();
      const id = env.LINKO_SESSIONS.idFromName(pairingCode);
      const stub = env.LINKO_SESSIONS.get(id);
      await stub.fetch(new Request(`https://session/pair/${pairingCode}`, { method: "POST" }));
      return json({ pairingCode });
    }

    if (url.pathname === "/ws") {
      const pairingCode = url.searchParams.get("code");
      if (!/^\d{6}$/.test(pairingCode)) return json({ error: "Invalid pairing code" }, 400);
      const id = env.LINKO_SESSIONS.idFromName(pairingCode);
      return env.LINKO_SESSIONS.get(id).fetch(request);
    }

    return new Response("Linko API", { headers: { "content-type": "text/plain" } });
  }
};

export class LinkoSession {
  constructor(state) {
    this.state = state;
    this.clients = new Set();
  }

  async fetch(request) {
    const url = new URL(request.url);

    if (request.method === "POST" && url.pathname.startsWith("/pair/")) {
      await this.state.storage.put("created", Date.now());
      return new Response("ok");
    }

    if (url.pathname !== "/ws" || request.headers.get("Upgrade") !== "websocket") {
      return new Response("Expected WebSocket", { status: 426 });
    }

    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair);
    server.accept();

    const entry = { ws: server };
    this.clients.add(entry);

    server.addEventListener("message", event => {
      for (const other of this.clients) {
        if (other.ws !== server && other.ws.readyState === WebSocket.OPEN) {
          other.ws.send(event.data);
        }
      }
    });

    const cleanup = () => this.clients.delete(entry);
    server.addEventListener("close", cleanup);
    server.addEventListener("error", cleanup);

    return new Response(null, { status: 101, webSocket: client });
  }
}
