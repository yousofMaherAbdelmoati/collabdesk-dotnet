import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "e2e",
  timeout: 30_000,
  use: { baseURL: process.env.COLLABDESK_E2E_URL ?? "http://127.0.0.1:5000", headless: true, launchOptions: { executablePath: "/usr/bin/google-chrome", args: ["--no-sandbox"] } }
});
