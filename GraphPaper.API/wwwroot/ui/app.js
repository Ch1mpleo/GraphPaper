window.GraphPaperUi = (() => {
    const tokenKey = "graphpaper.jwt";

    function getToken() {
        return localStorage.getItem(tokenKey) ?? "";
    }

    function setToken(token) {
        localStorage.setItem(tokenKey, token);
    }

    function clearToken() {
        localStorage.removeItem(tokenKey);
    }

    function unwrapData(payload) {
        if (payload && payload.value && payload.value.data !== undefined) {
            return payload.value.data;
        }

        if (payload && payload.data !== undefined) {
            return payload.data;
        }

        return payload;
    }

    function readMessage(payload) {
        return payload?.value?.message
            ?? payload?.message
            ?? payload?.error
            ?? "Request failed.";
    }

    async function apiFetch(path, options = {}) {
        const headers = options.headers ? { ...options.headers } : {};
        const token = getToken();

        if (token) {
            headers.Authorization = `Bearer ${token}`;
        }

        if (!(options.body instanceof FormData) && !headers["Content-Type"]) {
            headers["Content-Type"] = "application/json";
        }

        const response = await fetch(path, {
            ...options,
            headers
        });

        let payload = null;
        try {
            payload = await response.json();
        }
        catch {
            payload = null;
        }

        if (!response.ok) {
            throw new Error(readMessage(payload));
        }

        return payload;
    }

    function setStatus(elementId, message, isError = false) {
        const element = document.getElementById(elementId);
        if (!element) {
            return;
        }

        element.textContent = message;
        element.className = `status ${isError ? "error" : "success"}`;
    }

    async function renderMermaid(containerId, mermaidCode) {
        const container = document.getElementById(containerId);
        if (!container) {
            return;
        }

        container.innerHTML = "";
        const node = document.createElement("pre");
        node.className = "mermaid";
        node.textContent = mermaidCode;
        container.appendChild(node);

        if (!window.mermaid) {
            return;
        }

        window.mermaid.initialize({ startOnLoad: false, securityLevel: "loose" });
        await window.mermaid.run({ nodes: [node] });
    }

    return {
        apiFetch,
        clearToken,
        getToken,
        readMessage,
        renderMermaid,
        setStatus,
        setToken,
        unwrapData
    };
})();
