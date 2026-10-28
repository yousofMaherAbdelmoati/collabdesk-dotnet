import { readFile, readdir } from "node:fs/promises";
const index = await readFile(new URL("../dist/index.html", import.meta.url), "utf8");
const assets = await readdir(new URL("../dist/assets", import.meta.url));
if (!index.includes('id="root"') || !assets.some((file) => file.endsWith(".js"))) throw new Error("React build output is incomplete.");
console.log("Frontend build smoke check passed.");
