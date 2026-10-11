"use strict";

(() => {
  const TOKEN_KEY = "am-token";
  const $ = (id) => document.getElementById(id);
  let token = null;
  let timer = null;

  // The desktop app opens the panel as /#t=<token>; move it into session storage and out of the address bar.
  const hash = new URLSearchParams(location.hash.slice(1));
  if (hash.get("t")) {
    sessionStorage.setItem(TOKEN_KEY, hash.get("t"));
    history.replaceState(null, "", location.pathname);
  }
  token = sessionStorage.getItem(TOKEN_KEY);

  async function api(path, options = {}) {
    const response = await fetch(path, {
      ...options,
      headers: { "Authorization": `Bearer ${token}`, "Content-Type": "application/json" },
    });
    if (response.status === 401) {
      throw Object.assign(new Error("unauthorized"), { status: 401 });
    }
    const body = await response.json().catch(() => ({}));
    if (!response.ok) {
      throw new Error(body.error || `Request failed (${response.status})`);
    }
    return body;
  }

  function setConnection(state, text) {
    $("connection").dataset.state = state;
    $("connection-text").textContent = text;
  }

  function showLogin(message) {
    clearInterval(timer);
    $("app").hidden = true;
    $("login").hidden = false;
    $("login-error").hidden = !message;
    $("login-error").textContent = message || "";
    setConnection("offline", "Not connected");
    $("token").focus();
  }

  let toastTimer = null;
  function toast(text, isError = false) {
    const el = $("toast");
    el.textContent = text;
    el.classList.toggle("error", isError);
    el.classList.add("show");
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => el.classList.remove("show"), 2400);
  }

  const formatDuration = (seconds) => {
    const h = Math.floor(seconds / 3600);
    const m = Math.floor((seconds % 3600) / 60);
    const s = Math.floor(seconds % 60);
    return h > 0 ? `${h}h ${String(m).padStart(2, "0")}m` : `${m}m ${String(s).padStart(2, "0")}s`;
  };

  function el(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function renderAccount(account) {
    const item = el("li", "account");
    const info = el("div");
    info.append(el("div", "name", account.name));
    const detail = el("div", "detail");
    const badge = el("span", "badge", account.state);
    badge.dataset.state = account.state;
    detail.append(badge);
    if (account.detail) detail.append(document.createTextNode(" · " + account.detail));
    info.append(detail);

    const metrics = el("div", "metrics");
    if (account.state === "Running") {
      metrics.append(
        el("span", null, `${Math.round(account.memoryMb).toLocaleString()} MB`),
        el("span", null, `${account.cpuPercent.toFixed(1)}% CPU`),
        el("span", null, formatDuration(account.uptimeSeconds)));
    }

    const actions = el("div", "actions");
    const active = ["Running", "Launching", "Queued", "Disconnected", "Restarting"].includes(account.state);
    if (active) {
      actions.append(button("Restart", () => act(`/api/accounts/${account.id}/restart`, `Restarting ${account.name}`)));
      actions.append(button("Stop", () => act(`/api/accounts/${account.id}/stop`, `Stopped ${account.name}`), "danger"));
    } else {
      actions.append(button("Launch", () => act(`/api/accounts/${account.id}/launch`, `Launching ${account.name}`), "primary"));
    }

    item.append(info, metrics, actions);
    return item;
  }

  function button(label, onClick, variant) {
    const b = el("button", "btn" + (variant ? " " + variant : ""), label);
    b.type = "button";
    b.addEventListener("click", onClick);
    return b;
  }

  function render(status) {
    $("stat-running").textContent = status.running;
    $("stat-cpu").textContent = `${Math.round(status.cpuPercent)}%`;
    $("bar-cpu").style.width = `${Math.min(100, status.cpuPercent)}%`;
    $("stat-mem").textContent = `${status.memoryUsedGb.toFixed(1)} / ${status.memoryTotalGb.toFixed(1)} GB`;
    $("bar-mem").style.width = `${status.memoryTotalGb ? Math.min(100, (status.memoryUsedGb / status.memoryTotalGb) * 100) : 0}%`;
    $("stat-clients-mem").textContent = `${(status.clientsMemoryMb / 1024).toFixed(1)} GB`;

    const list = $("accounts");
    list.replaceChildren(...status.accounts.map(renderAccount));
    $("accounts-empty").hidden = status.accounts.length > 0;

    $("schedule-status").textContent = status.scheduledAction
      ? `${status.scheduledAction} in ${formatDuration(status.scheduledInSeconds)}`
      : "Nothing scheduled";
  }

  async function refresh() {
    try {
      render(await api("/api/status"));
      setConnection("online", "Connected");
    } catch (error) {
      if (error.status === 401) {
        sessionStorage.removeItem(TOKEN_KEY);
        showLogin("That token was not accepted.");
      } else {
        setConnection("offline", "Can't reach the app");
      }
    }
  }

  async function act(path, message, body) {
    try {
      await api(path, { method: "POST", body: JSON.stringify(body || {}) });
      toast(message);
      refresh();
    } catch (error) {
      toast(error.message, true);
    }
  }

  function start() {
    $("login").hidden = true;
    $("app").hidden = false;
    refresh();
    clearInterval(timer);
    timer = setInterval(refresh, 2000);
  }

  $("login-form").addEventListener("submit", (event) => {
    event.preventDefault();
    token = $("token").value.trim();
    if (!token) return;
    sessionStorage.setItem(TOKEN_KEY, token);
    start();
  });

  $("close-all").addEventListener("click", () => {
    if (confirm("Close every running Roblox client?")) act("/api/close-all", "Closing all clients");
  });

  document.querySelectorAll("[data-minutes]").forEach((b) => b.addEventListener("click", () => {
    const minutes = Number(b.dataset.minutes);
    if (confirm(`Shut down the PC in ${minutes} minutes?`)) act("/api/shutdown", "Shutdown scheduled", { minutes });
  }));

  $("custom-form").addEventListener("submit", (event) => {
    event.preventDefault();
    const minutes = Number($("custom-minutes").value);
    if (!Number.isInteger(minutes) || minutes < 1 || minutes > 2880) {
      toast("Enter between 1 and 2880 minutes.", true);
      return;
    }
    if (confirm(`Shut down the PC in ${minutes} minutes?`)) {
      act("/api/shutdown", "Shutdown scheduled", { minutes });
      $("custom-minutes").value = "";
    }
  });

  $("cancel-schedule").addEventListener("click", () => act("/api/shutdown", "Schedule cancelled", { cancel: true }));

  if (token) start(); else showLogin();
})();
