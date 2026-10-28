import { FormEvent, useCallback, useEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from "@microsoft/signalr";
import "./styles.css";

type Role = "Customer" | "Agent" | "Administrator";
type CurrentUser = { id: string; email: string; role: Role };
type Ticket = { id: number; title: string; description: string; status: string; customerId: string; assignedAgentId?: string; createdAt: string };
type TicketPage = { items: Ticket[]; page: number; pageSize: number; totalCount: number };
type ChatMessage = { id: number; senderId: string; clientMessageId: string; body: string; createdAt: string; ticketId: number };
type History = { messages: ChatMessage[]; nextAfterMessageId?: number };
type Report = { id: number; status: string; type: string; from: string; to: string; error?: string; resultExpiresAt?: string };

const api = async <T,>(path: string, csrf?: string, options: RequestInit = {}): Promise<T> => {
  const headers = new Headers(options.headers);
  if (csrf && options.method && options.method !== "GET") headers.set("X-CSRF-TOKEN", csrf);
  if (options.body) headers.set("Content-Type", "application/json");
  const response = await fetch(path, { credentials: "include", ...options, headers });
  if (!response.ok) {
    const problem = await response.json().catch(() => null) as { detail?: string; title?: string } | null;
    throw new Error(problem?.detail ?? problem?.title ?? `Request failed (${response.status})`);
  }
  return response.status === 204 ? undefined as T : response.json() as Promise<T>;
};

const getCsrf = () => api<{ token: string }>("/api/auth/csrf").then(result => result.token);
const ticketKey = (title: string, description: string) => `collabdesk.ticket.${title.trim()}\n${description.trim()}`;
const messageKey = (userId: string, ticketId: number, body: string) => `collabdesk.message.${userId}.${ticketId}.${body}`;

function App() {
  const [user, setUser] = useState<CurrentUser>();
  const [csrf, setCsrf] = useState("");
  const [tickets, setTickets] = useState<Ticket[]>([]);
  const [selected, setSelected] = useState<Ticket>();
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [connectionState, setConnectionState] = useState("offline");
  const [status, setStatus] = useState("Sign in with a fictional local fixture.");
  const [report, setReport] = useState<Report>();
  const [reportResult, setReportResult] = useState<unknown>();
  const [assistantText, setAssistantText] = useState("");
  const [streaming, setStreaming] = useState(false);
  const hub = useRef<HubConnection | null>(null);
  const chatGeneration = useRef(0);
  const assistantAbort = useRef<AbortController | null>(null);
  const pending = useRef(new Set<string>());
  const reportCache = useRef(new Map<number, unknown>());

  const loadTickets = useCallback(async () => {
    if (!user) return;
    const page = await api<TicketPage>("/api/tickets?page=1&pageSize=50");
    setTickets(page.items);
    setSelected(current => page.items.find(ticket => ticket.id === current?.id) ?? current);
  }, [user]);

  const closeChat = useCallback(async () => {
    chatGeneration.current++;
    const connection = hub.current; hub.current = null;
    if (connection) await connection.stop();
    setConnectionState("offline");
  }, []);

  const openChat = useCallback(async (ticket: Ticket) => {
    const generation = ++chatGeneration.current;
    const previous = hub.current; hub.current = null;
    if (previous) await previous.stop();
    if (generation !== chatGeneration.current) return;
    setMessages([]); setConnectionState("connecting");
    const connection = new HubConnectionBuilder().withUrl("/ws/tickets").withAutomaticReconnect([0, 1000, 3000, 5000]).configureLogging(LogLevel.Warning).build();
    const sync = async () => {
      let afterMessageId = 0;
      for (;;) {
        const history = await connection.invoke<History>("SyncHistory", ticket.id, afterMessageId, 100);
        if (generation !== chatGeneration.current) return;
        setMessages(existing => [...existing, ...history.messages.filter(message => message.ticketId === ticket.id && !existing.some(item => item.id === message.id))].sort((a, b) => a.id - b.id));
        if (!history.nextAfterMessageId) return;
        afterMessageId = history.nextAfterMessageId;
      }
    };
    connection.on("message", (message: ChatMessage) => {
      if (generation !== chatGeneration.current || message.ticketId !== ticket.id) return;
      setMessages(existing => existing.some(item => item.id === message.id) ? existing : [...existing, message].sort((a, b) => a.id - b.id));
    });
    connection.on("readReceipt", (receipt: { lastReadMessageId: number }) => setStatus(`Read state updated through message ${receipt.lastReadMessageId}.`));
    connection.onreconnecting(() => { if (generation === chatGeneration.current) setConnectionState("reconnecting"); });
    connection.onreconnected(() => void (async () => {
      if (generation !== chatGeneration.current) return;
      try { await connection.invoke("JoinTicket", ticket.id); await sync(); if (generation === chatGeneration.current) setConnectionState("connected"); }
      catch (error) { if (generation === chatGeneration.current) setStatus(error instanceof Error ? error.message : "Chat reconnection failed."); }
    })());
    connection.onclose(() => { if (generation === chatGeneration.current) setConnectionState("offline"); });
    hub.current = connection;
    try {
      await connection.start();
      if (generation !== chatGeneration.current) { await connection.stop(); return; }
      await connection.invoke("JoinTicket", ticket.id); await sync();
      if (generation === chatGeneration.current) setConnectionState("connected");
    } catch (error) {
      if (hub.current === connection) hub.current = null;
      await connection.stop();
      throw error;
    }
  }, []);

  useEffect(() => () => { void closeChat(); assistantAbort.current?.abort(); }, [closeChat]);
  useEffect(() => { void api<CurrentUser>("/api/auth/me").then(async current => { setUser(current); setCsrf(await getCsrf()); }).catch(() => undefined); }, []);
  useEffect(() => { if (user) void loadTickets().catch(error => setStatus(error.message)); }, [user, loadTickets]);
  useEffect(() => { if (selected && user?.role !== "Administrator") void openChat(selected).catch(error => setStatus(error.message)); }, [selected, user, openChat]);
  useEffect(() => {
    const last = messages.at(-1);
    if (last && selected && last.ticketId === selected.id && hub.current?.state === HubConnectionState.Connected) void hub.current.invoke("MarkRead", selected.id, last.id).catch(error => setStatus(error.message));
  }, [messages, selected]);

  const signIn = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault(); const form = new FormData(event.currentTarget);
    try {
      const token = await getCsrf();
      await api<void>("/api/auth/login", token, { method: "POST", body: JSON.stringify({ email: form.get("email"), password: form.get("password") }) });
      setCsrf(await getCsrf()); setUser(await api<CurrentUser>("/api/auth/me")); setStatus("Authenticated locally.");
    } catch (error) { setStatus(error instanceof Error ? error.message : "Login failed."); }
  };
  const signOut = async () => { await api<void>("/api/auth/logout", csrf, { method: "POST", body: "{}" }); await closeChat(); setUser(undefined); setTickets([]); setSelected(undefined); setMessages([]); setStatus("Signed out."); };

  const createTicket = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault(); const formElement = event.currentTarget; const form = new FormData(formElement); const title = String(form.get("title") ?? ""); const description = String(form.get("description") ?? ""); const storageKey = ticketKey(title, description);
    if (pending.current.has(storageKey)) return; pending.current.add(storageKey);
    const idempotencyKey = sessionStorage.getItem(storageKey) ?? crypto.randomUUID(); sessionStorage.setItem(storageKey, idempotencyKey);
    try {
      const ticket = await api<Ticket>("/api/tickets", csrf, { method: "POST", headers: { "Idempotency-Key": idempotencyKey }, body: JSON.stringify({ title, description }) });
      sessionStorage.removeItem(storageKey); formElement.reset(); setSelected(ticket); await loadTickets(); setStatus("Ticket saved; retry-safe key cleared.");
    } catch (error) { setStatus(error instanceof Error ? error.message : "Could not create ticket."); }
    finally { pending.current.delete(storageKey); }
  };
  const changeTicket = async (path: string) => {
    if (!selected || pending.current.has(path)) return; pending.current.add(path);
    try { await api<Ticket>(path, csrf, { method: "POST", body: path.endsWith("/status") ? JSON.stringify({ status: "Resolved" }) : "{}" }); await loadTickets(); setStatus("Ticket updated."); }
    catch (error) { setStatus(error instanceof Error ? error.message : "Ticket update failed."); }
    finally { pending.current.delete(path); }
  };
  const sendMessage = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault(); const formElement = event.currentTarget; const form = new FormData(formElement); const body = String(form.get("message") ?? "").trim();
    if (!body || !selected || !hub.current || hub.current.state !== HubConnectionState.Connected) return;
    const key = messageKey(user!.id, selected.id, body); const clientMessageId = sessionStorage.getItem(key) ?? crypto.randomUUID(); sessionStorage.setItem(key, clientMessageId);
    try { await hub.current.invoke("SendMessage", selected.id, clientMessageId, body); sessionStorage.removeItem(key); formElement.reset(); }
    catch (error) { setStatus(error instanceof Error ? error.message : "Message failed."); }
  };
  const startAssistant = async () => {
    if (!selected || assistantAbort.current) return; const controller = new AbortController(); assistantAbort.current = controller; setStreaming(true); setAssistantText("");
    try {
      const response = await fetch("/api/assistant/stream", { method: "POST", credentials: "include", signal: controller.signal, headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": csrf }, body: JSON.stringify({ ticketId: selected.id }) });
      if (!response.ok || !response.body) throw new Error("Assistant could not start.");
      const reader = response.body.getReader(); const decoder = new TextDecoder(); let buffer = "";
      for (;;) { const next = await reader.read(); if (next.done) break; buffer += decoder.decode(next.value, { stream: true }); const events = buffer.split("\n\n"); buffer = events.pop() ?? ""; events.forEach(event => { const data = event.split("\n").find(line => line.startsWith("data: "))?.slice(6); if (!data) return; const payload = JSON.parse(data) as { text?: string }; if (payload.text) setAssistantText(text => text + payload.text); }); }
    } catch (error) { if (!controller.signal.aborted) setStatus(error instanceof Error ? error.message : "Assistant failed."); }
    finally { assistantAbort.current = null; setStreaming(false); }
  };
  const cancelAssistant = () => { assistantAbort.current?.abort(); setStreaming(false); };
  const requestReport = async () => {
    if (pending.current.has("report")) return; pending.current.add("report"); const today = new Date().toISOString().slice(0, 10);
    try { const job = await api<Report>("/api/reports", csrf, { method: "POST", body: JSON.stringify({ type: "tickets", from: today, to: today }) }); setReport(job); setReportResult(undefined); setStatus(`Report ${job.id} queued.`); }
    catch (error) { setStatus(error instanceof Error ? error.message : "Report request failed."); }
    finally { pending.current.delete("report"); }
  };
  useEffect(() => { if (!report || ["Completed", "Failed", "Cancelled"].includes(report.status)) return; const timer = window.setInterval(() => api<Report>(`/api/reports/${report.id}`).then(setReport).catch(error => setStatus(error.message)), 1000); return () => window.clearInterval(timer); }, [report]);
  useEffect(() => { if (!report || report.status !== "Completed") return; const cached = reportCache.current.get(report.id); if (cached) { setReportResult(cached); return; } void api<unknown>(`/api/reports/${report.id}/result`).then(result => { reportCache.current.set(report.id, result); setReportResult(result); }).catch(error => setStatus(error.message)); }, [report]);
  const cancelReport = async () => { if (!report) return; await api<void>(`/api/reports/${report.id}`, csrf, { method: "DELETE" }); setReport(await api<Report>(`/api/reports/${report.id}`)); };

  if (!user) return <main className="auth"><h1>CollabDesk <small>local demo</small></h1><p>Fictional data only. Sessions stay in this browser.</p><form onSubmit={signIn}><label>Email<input name="email" type="email" defaultValue="demo-customer@collabdesk.test" required /></label><label>Password<input name="password" type="password" defaultValue="local-fixture-password" required /></label><button>Sign in</button></form><p className="status">{status}</p></main>;
  return <main><header><div><h1>CollabDesk <small>local demo</small></h1><span>{user.email} · {user.role}</span></div><button onClick={() => void signOut()}>Sign out</button></header><p className="status">{status}</p>{user.role === "Administrator" ? <section className="admin"><h2>Asynchronous reports</h2><p>Results are cached in this browser after completion; durable job state remains on the server.</p><button onClick={() => void requestReport()}>Request today’s ticket report</button>{report && <div className="card"><strong>Report #{report.id}: {report.status}</strong>{report.status === "Queued" && <button onClick={() => void cancelReport()}>Cancel queued report</button>}{report.error && <p>{report.error}</p>}{Boolean(reportResult) && <pre>{JSON.stringify(reportResult, null, 2)}</pre>}</div>}</section> : <section className="workspace"><aside><h2>{user.role === "Customer" ? "Your tickets" : "Available work"}</h2>{user.role === "Customer" && <form onSubmit={createTicket}><input name="title" placeholder="Ticket title" maxLength={160} required /><textarea name="description" placeholder="Describe the fictional issue" maxLength={4000} required /><button>Create ticket</button></form>}<button onClick={() => void loadTickets()}>Refresh list</button><ul>{tickets.map(ticket => <li key={ticket.id}><button className={selected?.id === ticket.id ? "selected" : ""} onClick={() => setSelected(ticket)}><b>#{ticket.id} {ticket.title}</b><span>{ticket.status}</span></button></li>)}</ul></aside><article>{selected ? <><div className="ticket-head"><div><h2>#{selected.id} {selected.title}</h2><p>{selected.description}</p></div>{user.role === "Agent" && <div>{selected.status === "Open" && <button onClick={() => void changeTicket(`/api/tickets/${selected.id}/claim`)}>Claim</button>}{selected.status === "Claimed" && selected.assignedAgentId === user.id && <button onClick={() => void changeTicket(`/api/tickets/${selected.id}/status`)}>Resolve</button>}</div>}</div><p className="connection">Chat: {connectionState}. Reconnect syncs persisted history; opening messages advances a durable read cursor.</p><div className="messages">{messages.map(message => <p key={message.id} className={message.senderId === user.id ? "mine" : ""}><small>{new Date(message.createdAt).toLocaleTimeString()}</small>{message.body}</p>)}</div><form className="message-form" onSubmit={sendMessage}><input name="message" placeholder="Write a message" maxLength={4000} required /><button disabled={connectionState !== "connected"}>Send</button></form>{user.role === "Customer" && <section className="assistant"><div className="assistant-notice" role="note"><strong>DEMO STREAM ONLY</strong><p>This is a fixed local SSE demonstration. It does not use AI, accept prompts, or process user-entered text.</p></div><div className="assistant-actions"><button onClick={() => void startAssistant()} disabled={streaming}>Start demo stream</button><button onClick={cancelAssistant} disabled={!streaming}>Cancel stream</button></div>{assistantText && <p aria-live="polite">{assistantText}</p>}</section>}</> : <p>Select a ticket to start.</p>}</article></section>}</main>;
}
createRoot(document.getElementById("root")!).render(<App />);
