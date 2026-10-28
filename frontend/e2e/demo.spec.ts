import { expect, test, type BrowserContext, type Page, type TestInfo } from "@playwright/test";

const localPassword = "local-fixture-password";

async function signIn(page: Page, email: string, role: string) {
  await page.goto("/");
  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Password").fill(localPassword);
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByText(`${email} · ${role}`)).toBeVisible();
}

async function closeContexts(...contexts: BrowserContext[]) {
  await Promise.all(contexts.map(context => context.close()));
}

test("NET-011: real browser contexts cover ticket collaboration, access control, SSE cancellation, and reports", async ({ browser }, testInfo: TestInfo) => {
  const customer = await browser.newContext();
  const agent = await browser.newContext();
  const administrator = await browser.newContext();

  try {
    const customerPage = await customer.newPage();
    const agentPage = await agent.newPage();
    const adminPage = await administrator.newPage();
    const title = `NET-011 fictional ticket ${Date.now()}`;
    const liveMessage = "NET-011 live SignalR message.";
    const offlineMessage = "NET-011 message retained during disconnect.";

    await signIn(customerPage, "demo-customer@collabdesk.test", "Customer");
    await customerPage.getByPlaceholder("Ticket title").fill(title);
    await customerPage.getByPlaceholder("Describe the fictional issue").fill("A fictional local-only E2E verification ticket.");
    await customerPage.getByRole("button", { name: "Create ticket" }).click();
    await expect(customerPage.getByRole("heading", { name: new RegExp(title) })).toBeVisible();
    await expect(customerPage.getByText("Chat: connected", { exact: false })).toBeVisible();

    const ticketId = Number((await customerPage.locator(".ticket-head h2").innerText()).match(/^#(\d+)/)?.[1]);
    expect(ticketId).toBeGreaterThan(0);

    // This is a real same-origin browser request with the customer's CSRF token.
    // The AgentOnly policy must reject it, rather than relying on UI affordances.
    const forbiddenStatus = await customerPage.evaluate(async id => {
      const csrf = await fetch("/api/auth/csrf", { credentials: "include" }).then(response => response.json());
      return fetch(`/api/tickets/${id}/claim`, {
        method: "POST",
        credentials: "include",
        headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": csrf.token },
        body: "{}"
      }).then(response => response.status);
    }, ticketId);
    expect(forbiddenStatus).toBe(403);

    await signIn(agentPage, "demo-agent@collabdesk.test", "Agent");
    await agentPage.getByRole("button", { name: "Refresh list" }).click();
    await agentPage.getByText(title, { exact: false }).first().click();
    await agentPage.getByRole("button", { name: "Claim", exact: true }).click();
    await expect(agentPage.getByText("Chat: connected", { exact: false })).toBeVisible();

    await agentPage.getByPlaceholder("Write a message").fill(liveMessage);
    await agentPage.getByRole("button", { name: "Send" }).click();
    await expect(customerPage.getByText(liveMessage)).toBeVisible();
    await expect(customerPage.getByText(/Read state updated through message/)).toBeVisible();
    await customerPage.screenshot({ path: testInfo.outputPath("net-011-customer-live-chat.png"), fullPage: true });

    // Closing the page stops its actual SignalR connection. A fresh page in the same
    // isolated browser context must load persisted history after it signs in again.
    await customerPage.close();
    await agentPage.getByPlaceholder("Write a message").fill(offlineMessage);
    await agentPage.getByRole("button", { name: "Send" }).click();
    await expect(agentPage.getByText(offlineMessage)).toBeVisible();

    const reconnectedCustomerPage = await customer.newPage();
    await signIn(reconnectedCustomerPage, "demo-customer@collabdesk.test", "Customer");
    await reconnectedCustomerPage.getByText(title, { exact: false }).first().click();
    await expect(reconnectedCustomerPage.getByText(offlineMessage)).toBeVisible();
    await expect(reconnectedCustomerPage.getByText("Chat: connected", { exact: false })).toBeVisible();
    await reconnectedCustomerPage.screenshot({ path: testInfo.outputPath("net-011-customer-reconnected-history.png"), fullPage: true });

    // Development intentionally paces the deterministic, local-only provider. This
    // keeps the real SSE response in flight long enough for AbortController to cancel it.
    const canceledRequest = reconnectedCustomerPage.waitForEvent("requestfailed", {
      predicate: request => request.url().includes("/api/assistant/stream")
    });
    await reconnectedCustomerPage.getByRole("button", { name: "Start assistant stream" }).click();
    await expect(reconnectedCustomerPage.getByRole("button", { name: "Cancel stream" })).toBeEnabled();
    await reconnectedCustomerPage.getByRole("button", { name: "Cancel stream" }).click();
    const canceled = await canceledRequest;
    expect(canceled.failure()?.errorText).toContain("ERR_ABORTED");
    await expect(reconnectedCustomerPage.getByRole("button", { name: "Start assistant stream" })).toBeEnabled();

    await signIn(adminPage, "demo-admin@collabdesk.test", "Administrator");
    await adminPage.getByRole("button", { name: "Request today’s ticket report" }).click();
    await expect(adminPage.getByText(/Report #\d+: Completed/)).toBeVisible({ timeout: 12_000 });
    await expect(adminPage.locator("pre")).toBeVisible();
    await adminPage.screenshot({ path: testInfo.outputPath("net-011-admin-report-completed.png"), fullPage: true });
  } finally {
    await closeContexts(customer, agent, administrator);
  }
});
