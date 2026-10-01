const SESSION_KEY = "openjibo_status_session";
const app = document.getElementById("app");

let draftValues = {};
let latestConfig = null;

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");
}

function token() {
  return localStorage.getItem(SESSION_KEY);
}

async function apiFetch(path, options = {}) {
  const response = await fetch(path, {
    ...options,
    headers: {
      ...(options.body ? { "Content-Type": "application/json" } : {}),
      ...(token() ? { Authorization: `Bearer ${token()}` } : {}),
      ...(options.headers || {}),
    },
  });
  const payload = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(payload.error || payload.details?.join?.(" ") || "Admin request failed.");
  return payload;
}

async function renderLogin(message = "") {
  app.innerHTML = `
    <div class="center-shell">
      <section class="card login-card">
        <p class="eyebrow">BEefy Admin</p>
        <h1>Server configuration</h1>
        <p class="lede">Enter the admin password to view and edit OpenJibo settings.</p>
        <label for="password">Admin password</label>
        <input id="password" type="password" autocomplete="current-password">
        <div class="button-row"><button class="button primary" id="login" type="button">Open config</button></div>
        ${message ? `<p class="status error">${escapeHtml(message)}</p>` : ""}
      </section>
    </div>`;
  const login = async () => {
    try {
      const payload = await apiFetch("/api/portal/status/login", {
        method: "POST",
        body: JSON.stringify({ password: document.getElementById("password").value }),
      });
      localStorage.setItem(SESSION_KEY, payload.portalSessionToken);
      await renderConfig();
    } catch (error) {
      await renderLogin(error.message);
    }
  };
  document.getElementById("login").addEventListener("click", login);
  document.getElementById("password").addEventListener("keydown", (event) => {
    if (event.key === "Enter") login();
  });
  document.getElementById("password").focus();
}

function settingControl(setting) {
  const current = Object.prototype.hasOwnProperty.call(draftValues, setting.key)
    ? draftValues[setting.key]
    : (setting.value ?? "");
  const inputId = `cfg-${setting.key.replaceAll(":", "-")}`;
  if (setting.valueType === "boolean") {
    const checked = String(current).toLowerCase() === "true";
    return `
      <label class="config-toggle" for="${inputId}">
        <input id="${inputId}" type="checkbox" data-key="${escapeHtml(setting.key)}" data-type="boolean" ${checked ? "checked" : ""}>
        <span>${checked ? "Enabled" : "Disabled"}</span>
      </label>`;
  }

  return `<input id="${inputId}" data-key="${escapeHtml(setting.key)}" data-type="${escapeHtml(setting.valueType)}"
    value="${escapeHtml(current)}" placeholder="${escapeHtml(setting.placeholder || setting.defaultValue || "")}">`;
}

function sectionHtml(section) {
  return `
    <section class="card panel tight">
      <div class="panel-header">
        <div><p class="eyebrow">Section</p><h2>${escapeHtml(section.name)}</h2></div>
        <span class="badge neutral">${section.settings.length} settings</span>
      </div>
      <div class="config-list">
        ${section.settings.map((setting) => `
          <div class="config-row">
            <div class="config-meta">
              <strong>${escapeHtml(setting.label)}</strong>
              <div class="muted-row">${escapeHtml(setting.description)}</div>
              <div class="muted-row"><code>${escapeHtml(setting.key)}</code></div>
              <div class="button-row" style="margin-top:0.4rem;">
                <span class="badge ${setting.isOverridden ? "warning" : "neutral"}">${escapeHtml(setting.source)}</span>
                ${setting.requiresRestart ? `<span class="badge warning">Restart required</span>` : ""}
                ${setting.isSecret ? `<span class="badge neutral">Secret</span>` : ""}
              </div>
            </div>
            <div class="config-control">${settingControl(setting)}</div>
          </div>`).join("")}
      </div>
    </section>`;
}

async function renderConfig(message = "", tone = "success") {
  let config;
  try {
    config = await apiFetch("/api/portal/config");
  } catch (error) {
    localStorage.removeItem(SESSION_KEY);
    await renderLogin(error.message);
    return;
  }

  latestConfig = config;
  if (Object.keys(draftValues).length === 0) {
    for (const section of config.sections || []) {
      for (const setting of section.settings || []) {
        draftValues[setting.key] = setting.value ?? "";
      }
    }
  }

  app.innerHTML = `
    <div class="status-shell">
      <section class="card status-hero">
        <div class="status-hero-top">
          <div>
            <p class="status-kicker">BEefy Admin</p>
            <h1>Server configuration</h1>
            <p class="status-lede">Edits write to a separate overlay file and take effect after you restart BEefy.</p>
          </div>
          <div class="button-row" style="margin-top:0;">
            <a class="secondary-button" href="/portal/status">Status</a>
            <a class="secondary-button" href="/portal/admin/onboarding">Onboarding</a>
            <a class="secondary-button" href="/portal/admin/harness">Harness</a>
            <button class="button danger" id="signOut" type="button">Sign out</button>
          </div>
        </div>
        <div class="stat-grid" style="margin-top:1rem;">
          <div class="stat-card"><span class="muted">Overlay</span><strong>${escapeHtml(config.overlayPath)}</strong></div>
          <div class="stat-card"><span class="muted">Restart</span><strong>${config.restartRequired ? "Required" : "Not pending"}</strong></div>
        </div>
      </section>
      ${(config.sections || []).map(sectionHtml).join("")}
      <section class="card panel tight">
        <div class="button-row">
          <button class="button primary" id="saveConfig" type="button">Save configuration</button>
          <button class="button secondary" id="reloadConfig" type="button">Reload</button>
          <button class="button danger" id="clearOverlay" type="button">Clear overlay</button>
        </div>
        ${message ? `<p class="status ${tone}" style="margin-top:1rem;">${escapeHtml(message)}</p>` : ""}
        ${config.restartRequired ? `<p class="status warning" style="margin-top:1rem;">Overlay changes are saved on disk. Restart the BEefy process for them to take effect.</p>` : ""}
      </section>
    </div>`;

  for (const input of app.querySelectorAll("[data-key]")) {
    const sync = () => {
      if (input.type === "checkbox") {
        draftValues[input.dataset.key] = input.checked ? "true" : "false";
        const label = input.parentElement?.querySelector("span");
        if (label) label.textContent = input.checked ? "Enabled" : "Disabled";
      } else {
        draftValues[input.dataset.key] = input.value;
      }
    };
    input.addEventListener("input", sync);
    input.addEventListener("change", sync);
  }

  document.getElementById("signOut").addEventListener("click", async () => {
    localStorage.removeItem(SESSION_KEY);
    await renderLogin();
  });

  document.getElementById("reloadConfig").addEventListener("click", async () => {
    draftValues = {};
    await renderConfig("Reloaded current effective configuration.");
  });

  document.getElementById("clearOverlay").addEventListener("click", async () => {
    if (!confirm("Clear the admin config overlay? Baseline appsettings/env will apply after restart.")) return;
    try {
      const result = await apiFetch("/api/portal/config", { method: "DELETE" });
      draftValues = {};
      await renderConfig(result.message || "Overlay cleared.", "warning");
    } catch (error) {
      await renderConfig(error.message, "error");
    }
  });

  document.getElementById("saveConfig").addEventListener("click", async () => {
    try {
      const values = {};
      for (const section of latestConfig.sections || []) {
        for (const setting of section.settings || []) {
          const next = draftValues[setting.key] ?? "";
          const current = setting.value ?? "";
          if (String(next) !== String(current)) {
            values[setting.key] = next === "" ? null : String(next);
          }
        }
      }

      if (Object.keys(values).length === 0) {
        await renderConfig("No changes to save.");
        return;
      }

      const result = await apiFetch("/api/portal/config", {
        method: "PUT",
        body: JSON.stringify({ values }),
      });
      draftValues = {};
      await renderConfig(result.message || "Configuration saved.", result.restartRequired ? "warning" : "success");
    } catch (error) {
      await renderConfig(error.message, "error");
    }
  });
}

if (token()) renderConfig().catch((error) => renderLogin(error.message));
else renderLogin();
