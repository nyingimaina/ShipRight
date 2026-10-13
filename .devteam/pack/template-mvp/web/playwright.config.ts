import { defineConfig, devices } from "@playwright/test";

const apiPort = 5080;
const webPort = 3100;

export default defineConfig({
  testDir: "./e2e",
  workers: 1,
  reporter: [["list"]],
  use: { baseURL: `http://localhost:${webPort}`, trace: "retain-on-failure" },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
  webServer: [
    {
      command: "dotnet run --project ../api/src --no-launch-profile",
      url: `http://localhost:${apiPort}/health`,
      reuseExistingServer: !process.env.CI,
      timeout: 180_000,
      env: {
        ASPNETCORE_URLS: `http://localhost:${apiPort}`,
        ASPNETCORE_ENVIRONMENT: "Development",
        AUTH_MODE: "multi", // these tests sign people up; the app's default is no sign-in
        // The tests sign up real users; they keep them in a folder of their own, never in the app's real data.
        Data__Directory: ".e2e-data",
      },
    },
    {
      command: `npm run dev -- --port ${webPort}`,
      url: `http://localhost:${webPort}/login`,
      reuseExistingServer: !process.env.CI,
      timeout: 180_000,
      env: { API_URL: `http://localhost:${apiPort}` },
    },
  ],
});
