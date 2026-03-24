window.GraphPaperUi = (() => {
    const tokenKey = "graphpaper.jwt";

    function getToken() { return localStorage.getItem(tokenKey) ?? ""; }
    function setToken(token) { localStorage.setItem(tokenKey, token); }
    function clearToken() { localStorage.removeItem(tokenKey); }

    function unwrapData(payload) {
        if (payload?.value?.data !== undefined) return payload.value.data;
        if (payload?.data !== undefined) return payload.data;
        return payload;
    }

    function readMessage(payload) {
        return payload?.value?.message ?? payload?.message ?? payload?.error ?? "Request failed.";
    }

    async function apiFetch(path, options = {}) {
        const headers = options.headers ? { ...options.headers } : {};
        const token = getToken();
        if (token) headers.Authorization = `Bearer ${token}`;
        if (!(options.body instanceof FormData) && !headers["Content-Type"])
            headers["Content-Type"] = "application/json";

        const response = await fetch(path, { ...options, headers });
        let payload = null;
        try { payload = await response.json(); } catch { payload = null; }
        if (!response.ok) throw new Error(readMessage(payload));
        return payload;
    }

    function setStatus(elementId, message, isError = false) {
        const el = document.getElementById(elementId);
        if (!el) return;
        el.textContent = message;
        el.className = `status ${isError ? "error" : "success"}`;
    }

    async function renderMermaid(containerId, mermaidCode, entityIndex = {}, onNodeClick = null) {
        const container = document.getElementById(containerId);
        if (!container) return;

        container.innerHTML = "";
        const pre = document.createElement("pre");
        pre.className = "mermaid";
        pre.textContent = mermaidCode;
        container.appendChild(pre);

        if (!window.mermaid) return;

        window.mermaid.initialize({ startOnLoad: false, securityLevel: "loose" });
        await window.mermaid.run({ nodes: [pre] });

        if (!onNodeClick || Object.keys(entityIndex).length === 0) return;

        const svg = container.querySelector("svg");
        if (!svg) return;

        svg.querySelectorAll("g.node").forEach(g => {
            // Mermaid node IDs look like "flowchart-NODEID-42" — strip both affixes
            const rawId = (g.id ?? "")
                .replace(/^flowchart-/, "")
                .replace(/-\d+$/, "");

            const entityData = entityIndex[rawId];
            if (!entityData) return;

            g.style.cursor = "pointer";
            g.addEventListener("click", () => onNodeClick(entityData));
        });
    }

    function showEntityPanel(entity) {
        document.getElementById("panelName").textContent = entity.name ?? "";
        document.getElementById("panelType").textContent = entity.entityType ?? "";
        document.getElementById("panelDescription").textContent = entity.description || "(No description)";
        document.getElementById("entityPanel").style.display = "flex";
    }

    function hideEntityPanel() {
        document.getElementById("entityPanel").style.display = "none";
    }

    return {
        apiFetch, clearToken, getToken, hideEntityPanel,
        readMessage, renderMermaid, setStatus, setToken,
        showEntityPanel, unwrapData
    };
})();
