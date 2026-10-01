const SESSION_KEY = "beefy.portal.session";

const app = document.querySelector("#app");
let sessionToken = localStorage.getItem(SESSION_KEY) || "";
let account = null;
let robots = [];
let setup = null;
let setupTimer = 0;
let view = sessionToken ? "home" : "login";
let message = "";

async function api(path, options = {}) {
  const headers = { ...(options.headers || {}) };
  if (options.body && !headers["Content-Type"]) headers["Content-Type"] = "application/json";
  if (sessionToken) headers.Authorization = `Bearer ${sessionToken}`;
  const response = await fetch(path, { ...options, headers });
  const text = await response.text();
  const payload = text ? JSON.parse(text) : {};
  if (!response.ok) {
    const error = new Error(payload.error || `Request failed (${response.status})`);
    error.payload = payload;
    throw error;
  }
  return payload;
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;");
}

function field(name, label, type = "text", value = "") {
  return `<label>${escapeHtml(label)}<input name="${name}" type="${type}" value="${escapeHtml(value)}" autocomplete="off"></label>`;
}

function render() {
  app.innerHTML = view === "home" ? homeHtml() : authHtml();
  bind();
}

function authHtml() {
  const title = view === "register" ? "Create account" : view === "reset" ? "Reset password" : "Sign in";
  const form = view === "register"
    ? `${field("email", "Email", "email")}${field("password", "Password", "password")}${field("firstName", "First name")}${field("lastName", "Last name")}`
    : view === "reset"
      ? `${field("email", "Email", "email")}${field("code", "Reset code")}${field("password", "New password", "password")}`
      : `${field("email", "Email", "email")}${field("password", "Password", "password")}`;
  const links = view === "login"
    ? `<button class="button secondary" type="button" data-view="register">Create account</button>
       <button class="button secondary" type="button" data-view="reset">Reset password</button>`
    : `<button class="button secondary" type="button" data-view="login">Back to sign in</button>`;
  return `<div class="center-shell"><form class="card login-card" id="auth-form">
    <p class="eyebrow">BEefy</p>
    <h1>${title}</h1>
    <p class="lede">${escapeHtml(message)}</p>
    ${form}
    <div class="button-row">
      <button class="button primary" type="submit">${view === "register" ? "Register" : view === "reset" ? "Save password" : "Sign in"}</button>
      ${links}
    </div>
  </form></div>`;
}

function homeHtml() {
  const name = [account?.firstName, account?.lastName].filter(Boolean).join(" ") || account?.email || "Account";
  const robotItems = robots.length
    ? robots.map(robot => `<li>
        <strong>${escapeHtml(robot.friendlyName || robot.robotId)}</strong>
        <span class="muted">${escapeHtml(robot.deviceId)}</span>
        <form class="inline-form" data-rename="${escapeHtml(robot.deviceId)}">
          <input name="name" value="${escapeHtml(robot.friendlyName || "")}" maxlength="64">
          <button class="button secondary" type="submit">Rename</button>
        </form>
      </li>`).join("")
    : `<li class="muted">No robots paired yet.</li>`;
  const setupBlock = setup
    ? `<div class="setup-qr">${setup.qrSvg || ""}</div>
       <p class="muted">Token <code>${escapeHtml(setup.token)}</code></p>
       <p>${setup.complete ? "Setup complete." : setup.expired ? "That setup token expired." : "Waiting for the robot to scan this code."}</p>`
    : "";
  return `<div class="shell">
    <header class="dashboard-header">
      <div>
        <p class="eyebrow">Household</p>
        <h1>${escapeHtml(name)}</h1>
        <p class="muted">${escapeHtml(account?.email || "")}</p>
      </div>
      <button class="button secondary" id="sign-out" type="button">Sign out</button>
    </header>
    <p class="lede">${escapeHtml(message)}</p>
    <div class="grid two">
      <section class="card panel">
        <h2>Profile</h2>
        <form id="profile-form">
          ${field("firstName", "First name", "text", account?.firstName || "")}
          ${field("lastName", "Last name", "text", account?.lastName || "")}
          <label>Gender
            <input name="gender" value="${escapeHtml(account?.gender || "")}">
          </label>
          <div class="button-row"><button class="button primary" type="submit">Save profile</button></div>
        </form>
        <form id="password-form">
          ${field("oldPassword", "Current password", "password")}
          ${field("newPassword", "New password", "password")}
          <div class="button-row"><button class="button secondary" type="submit">Change password</button></div>
        </form>
      </section>
      <section class="card panel">
        <h2>Robots</h2>
        <ul>${robotItems}</ul>
        <form id="claim-form">
          ${field("code", "Claim code")}
          <div class="button-row"><button class="button secondary" type="submit">Claim robot</button></div>
        </form>
        <form id="setup-form">
          <div class="button-row"><button class="button primary" type="submit">Start QR setup</button></div>
        </form>
        ${setupBlock}
      </section>
    </div>
  </div>`;
}

function bind() {
  document.querySelectorAll("[data-view]").forEach(button => {
    button.addEventListener("click", () => {
      view = button.dataset.view;
      message = "";
      render();
    });
  });
  document.querySelector("#auth-form")?.addEventListener("submit", onAuth);
  document.querySelector("#sign-out")?.addEventListener("click", () => {
    sessionToken = "";
    account = null;
    localStorage.removeItem(SESSION_KEY);
    window.clearInterval(setupTimer);
    view = "login";
    message = "";
    render();
  });
  document.querySelector("#profile-form")?.addEventListener("submit", onProfile);
  document.querySelector("#password-form")?.addEventListener("submit", onPassword);
  document.querySelector("#claim-form")?.addEventListener("submit", onClaim);
  document.querySelector("#setup-form")?.addEventListener("submit", onSetup);
  document.querySelectorAll("[data-rename]").forEach(form => {
    form.addEventListener("submit", event => onRename(event, form.dataset.rename));
  });
}

async function onAuth(event) {
  event.preventDefault();
  const data = new FormData(event.target);
  try {
    if (view === "reset") {
      if (!data.get("code")) {
        const requested = await api("/api/portal/account/password-reset", {
          method: "POST",
          body: JSON.stringify({ email: data.get("email") })
        });
        message = requested.code
          ? `Reset code: ${requested.code}`
          : "If that email has an account, a reset code is ready.";
        render();
        return;
      }
      await api("/api/portal/account/password-reset/confirm", {
        method: "POST",
        body: JSON.stringify({ code: data.get("code"), password: data.get("password") })
      });
      view = "login";
      message = "Password updated. Sign in with the new password.";
      render();
      return;
    }

    const path = view === "register" ? "/api/portal/account/register" : "/api/portal/account/login";
    const payload = await api(path, {
      method: "POST",
      body: JSON.stringify({
        email: data.get("email"),
        password: data.get("password"),
        firstName: data.get("firstName"),
        lastName: data.get("lastName")
      })
    });
    rememberSession(payload);
    message = "";
    await loadHome();
  } catch (error) {
    message = error.message;
    render();
  }
}

function rememberSession(payload) {
  sessionToken = payload.portalSessionToken;
  account = payload.user;
  localStorage.setItem(SESSION_KEY, sessionToken);
  view = "home";
}

async function loadHome() {
  account = await api("/api/portal/account/me");
  const listed = await api("/api/portal/robots");
  robots = listed.robots || [];
  view = "home";
  render();
}

async function onProfile(event) {
  event.preventDefault();
  const data = new FormData(event.target);
  try {
    account = await api("/api/portal/account/profile", {
      method: "PUT",
      body: JSON.stringify({
        firstName: data.get("firstName"),
        lastName: data.get("lastName"),
        gender: data.get("gender")
      })
    });
    message = "Profile saved.";
    render();
  } catch (error) {
    message = error.message;
    render();
  }
}

async function onPassword(event) {
  event.preventDefault();
  const data = new FormData(event.target);
  try {
    await api("/api/portal/account/password", {
      method: "POST",
      body: JSON.stringify({
        oldPassword: data.get("oldPassword"),
        newPassword: data.get("newPassword")
      })
    });
    message = "Password changed.";
    render();
  } catch (error) {
    message = error.message;
    render();
  }
}

async function onClaim(event) {
  event.preventDefault();
  const data = new FormData(event.target);
  try {
    await api("/api/portal/robots/pair", {
      method: "POST",
      body: JSON.stringify({ code: data.get("code"), portalSessionToken: sessionToken })
    });
    message = "Robot claimed.";
    await loadHome();
  } catch (error) {
    message = error.message;
    render();
  }
}

async function onRename(event, deviceId) {
  event.preventDefault();
  const data = new FormData(event.target);
  try {
    await api(`/api/portal/robots/${encodeURIComponent(deviceId)}/name`, {
      method: "PUT",
      body: JSON.stringify({ name: data.get("name"), portalSessionToken: sessionToken })
    });
    message = "Robot renamed.";
    await loadHome();
  } catch (error) {
    message = error.message;
    render();
  }
}

async function onSetup(event) {
  event.preventDefault();
  try {
    const started = await api("/api/portal/robots/setup", { method: "POST" });
    setup = { token: started.token, qrSvg: started.qrSvg, complete: false, expired: false };
    message = "Show this code to the robot.";
    window.clearInterval(setupTimer);
    setupTimer = window.setInterval(pollSetup, 2000);
    render();
  } catch (error) {
    message = error.message;
    render();
  }
}

async function pollSetup() {
  if (!setup?.token) return;
  try {
    const status = await api(`/api/portal/robots/setup/status?token=${encodeURIComponent(setup.token)}`);
    setup.complete = Boolean(status.complete);
    setup.expired = Boolean(status.expired);
    if (setup.complete || setup.expired) {
      window.clearInterval(setupTimer);
      if (setup.complete) await loadHome();
      else render();
    }
  } catch (error) {
    message = error.message;
    window.clearInterval(setupTimer);
    render();
  }
}

if (sessionToken) {
  loadHome().catch(() => {
    sessionToken = "";
    localStorage.removeItem(SESSION_KEY);
    view = "login";
    render();
  });
} else {
  render();
}
