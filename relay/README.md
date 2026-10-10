# Conduit Relay

A tiny, stateless rendezvous server so your iPhone can reach Conduit Host from anywhere — behind NAT, CGNAT
or a corporate firewall — without port forwarding. The host keeps one outbound WebSocket to the relay; when the
phone connects, the relay asks the host to dial back and pipes the two sockets together.

**The relay never sees plaintext.** Phone and host run an authenticated P-256 / AES-256-GCM handshake end to end
(see [../docs/PROTOCOL.md](../docs/PROTOCOL.md)). Hosts prove ownership of their id with an ECDSA signature, so
nobody can impersonate your computer on the relay.

## Run

```bash
# Automatic HTTPS with Caddy (needs ports 80/443 and a DNS record pointing at the server)
RELAY_DOMAIN=relay.example.com docker compose up -d

# Or just the relay (put your own TLS proxy in front)
docker build -t conduit-relay . && docker run -p 8080:8080 conduit-relay

# Or without Docker
PORT=8080 dotnet run --project src -c Release
```

Tagged releases also publish `ghcr.io/<owner>/conduit-relay`.

Then in **Conduit Host → Settings → Relay URL** enter `wss://relay.example.com`, save, and re-pair (or just
reconnect — the phone learns the relay from `host.info`).

`GET /healthz` returns `{ ok, hosts, pending }`.
