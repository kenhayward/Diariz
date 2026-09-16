/**
 * apps/web/nginx.conf is a deliverable in its own right: it is the front door of every deployment, and the
 * properties below are invisible when they break - nothing errors, nginx -t still passes, and the app keeps
 * working. So they are asserted on the file itself, the same way manifest.test.ts pins the manifest blocks.
 */
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

const conf = () => readFileSync(join(__dirname, "..", "..", "nginx.conf"), "utf8");

/** Directives only - comments are prose and routinely mention the very things being ruled out. */
const directives = () =>
  conf()
    .split("\n")
    .map((line) => line.replace(/#.*$/, ""))
    .join("\n");

/** Every `location ... { ... }` block's body. None of these blocks nest braces. */
const locations = () => [...directives().matchAll(/location\s+[^{]+\{([^}]*)\}/g)].map((m) => m[1]);

describe("nginx forwarded-scheme trust", () => {
  it("never passes a caller's X-Forwarded-Proto through unconditionally", () => {
    // The API believes X-Forwarded-Proto from this container. If nginx relays whatever any caller sent, anyone
    // reaching the published web port can claim https.
    expect(directives()).not.toMatch(/default\s+\$http_x_forwarded_proto\s*;/);
  });

  it("decides trust from the connecting address, limited to loopback and private networks", () => {
    const d = directives();
    const geo = d.match(/geo\s+\$remote_addr\s+\$from_private_network\s*\{([^}]*)\}/);
    expect(geo).not.toBeNull();
    const body = geo![1];
    expect(body).toMatch(/default\s+0\s*;/);
    for (const net of ["127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "::1", "fc00::/7"]) {
      expect(body).toContain(`${net} 1;`);
    }
  });

  it("only ever forwards http or https", () => {
    const map = directives().match(/map\s+"\$from_private_network:\$http_x_forwarded_proto"\s+\$client_proto\s*\{([^}]*)\}/);
    expect(map).not.toBeNull();
    expect(map![1]).toMatch(/default\s+\$scheme\s*;/);
    expect(map![1]).toContain('"~^1:(https?)$"');
  });
});

describe("nginx never records credentials", () => {
  it("logs through a format with no query string or Referer", () => {
    // SignalR, <audio>/<img> and the backup download carry ?access_token=<JWT>. The default `combined` format
    // logs $request, which includes it.
    const d = directives();
    const format = d.match(/log_format\s+(\w+)\s+([^;]*);/);
    expect(format).not.toBeNull();
    const [, name, body] = format!;
    for (const variable of ["$request ", "$request\"", "$request_uri", "$args", "$query_string", "$http_referer"]) {
      expect(body).not.toContain(variable);
    }
    expect(d).toMatch(new RegExp(`access_log\\s+\\S+\\s+${name}\\s*;`));
  });

  it("sends Referrer-Policy: no-referrer from the server and from every location that sets its own headers", () => {
    // add_header in a location REPLACES the inherited server-level headers rather than adding to them, so a
    // location with any add_header must repeat this one or it silently goes missing there.
    const header = /add_header\s+Referrer-Policy\s+"no-referrer"\s+always\s*;/;
    const serverLevel = directives().replace(/location\s+[^{]+\{[^}]*\}/g, "");
    expect(serverLevel).toMatch(header);
    for (const body of locations().filter((b) => /add_header/.test(b))) {
      expect(body).toMatch(header);
    }
  });
});
