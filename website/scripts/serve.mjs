import { createReadStream, statSync } from "node:fs";
import { createServer } from "node:http";
import { extname, join, normalize } from "node:path";

const root = normalize(new URL("..", import.meta.url).pathname.replace(/^\/(.:)/, "$1"));
const port = Number(process.env.PORT ?? 4173);
const types = { ".css": "text/css", ".html": "text/html", ".js": "text/javascript", ".png": "image/png" };

createServer((request, response) => {
  const relative = decodeURIComponent(new URL(request.url ?? "/", "http://localhost").pathname);
  let path = join(root, relative === "/" ? "index.html" : relative);
  try {
    if (!normalize(path).startsWith(root) || statSync(path).isDirectory()) throw new Error("not found");
  } catch {
    path = join(root, "404.html");
    response.statusCode = 404;
  }
  response.setHeader("content-type", `${types[extname(path)] ?? "application/octet-stream"}; charset=utf-8`);
  createReadStream(path).pipe(response);
}).listen(port, "127.0.0.1", () => console.log(`FloorballDJ website: http://127.0.0.1:${port}`));
