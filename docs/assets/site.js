// Empire Engine docs: builds the top bar, the sidebar, the "On this page" list and the previous/next links, and
// colors C# code. Every page includes this file; to add a page, write it and list it in NAV below.

const NAV = [
  {
    group: "Start here",
    pages: [
      ["index.html", "Overview"],
      ["getting-started.html", "Getting started"],
      ["projects.html", "Projects and Empire Engine Hub"],
    ],
  },
  {
    group: "Using the editor",
    pages: [
      ["editor.html", "The editor"],
      ["scenes.html", "Scenes and objects"],
      ["assets.html", "Models, textures and sound files"],
      ["terminal.html", "The terminal"],
      ["assistant.html", "The AI assistant (optional)"],
    ],
  },
  {
    group: "Scripting",
    pages: [
      ["scripting.html", "C# scripting"],
      ["input.html", "Input and time"],
      ["visual-scripting.html", "Visual scripting"],
      ["custom-components.html", "Custom components"],
      ["scene-management.html", "Loading scenes and saving games"],
    ],
  },
  {
    group: "Systems",
    pages: [
      ["rendering.html", "Rendering and lighting"],
      ["physics.html", "Physics"],
      ["audio.html", "Audio"],
      ["animation.html", "Animation"],
      ["terrain.html", "Terrain"],
      ["particles.html", "Particle systems"],
      ["game-ui.html", "Game UI"],
    ],
  },
  {
    group: "Extending and shipping",
    pages: [
      ["packages.html", "Packages"],
      ["building.html", "Building and publishing"],
    ],
  },
  {
    group: "Reference",
    pages: [
      ["components.html", "Component reference"],
      ["api.html", "Scripting API reference"],
      ["file-formats.html", "File formats"],
      ["architecture.html", "Engine architecture"],
    ],
  },
];

const currentFile = (() => {
  const last = location.pathname.split("/").pop();
  return last && last.endsWith(".html") ? last : "index.html";
})();

function el(tag, attrs = {}, children = []) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs)) {
    if (key === "text") node.textContent = value;
    else if (key === "html") node.innerHTML = value;
    else node.setAttribute(key, value);
  }
  for (const child of children) node.append(child);
  return node;
}

// ---- theme ----------------------------------------------------------------------------------

function storedTheme() {
  try { return localStorage.getItem("empire-docs-theme"); } catch { return null; }
}

function applyTheme(theme) {
  if (theme === "light" || theme === "dark") document.documentElement.setAttribute("data-theme", theme);
  else document.documentElement.removeAttribute("data-theme");
}

function effectiveTheme() {
  const set = document.documentElement.getAttribute("data-theme");
  if (set) return set;
  return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}

applyTheme(storedTheme());

// ---- top bar and sidebar ----------------------------------------------------------------------

function buildTopbar() {
  const menu = el("button", { class: "icon-btn menu-btn", "aria-label": "Open the page list", text: "☰" });
  menu.addEventListener("click", () => document.body.classList.toggle("nav-open"));

  const theme = el("button", { class: "icon-btn", "aria-label": "Switch between light and dark" });
  const label = () => { theme.textContent = effectiveTheme() === "dark" ? "☀ Light" : "☾ Dark"; };
  label();
  theme.addEventListener("click", () => {
    const next = effectiveTheme() === "dark" ? "light" : "dark";
    applyTheme(next);
    try { localStorage.setItem("empire-docs-theme", next); } catch { /* private window: the choice lasts this page */ }
    label();
  });

  const brand = el("a", { class: "brand", href: "index.html" }, [
    el("span", { class: "brand-mark", text: "E" }),
    el("span", { text: "EMPIRE" }),
    el("small", { text: "Engine docs · v0.1.5" }),
  ]);

  return el("header", { class: "topbar" }, [menu, brand, el("div", { class: "topbar-spacer" }), theme]);
}

function buildSidebar(nav) {
  const search = el("input", { class: "search", type: "search", placeholder: "Filter pages…", "aria-label": "Filter pages" });
  nav.append(search);

  const groups = [];
  for (const { group, pages } of NAV) {
    const box = el("div", { class: "nav-group" }, [el("h4", { text: group })]);
    for (const [file, title] of pages) {
      const link = el("a", { href: file, text: title });
      if (file === currentFile) link.classList.add("active");
      box.append(link);
    }
    nav.append(box);
    groups.push(box);
  }
  const empty = el("p", { class: "nav-empty", text: "No page matches." });
  empty.hidden = true;
  nav.append(empty);

  search.addEventListener("input", () => {
    const q = search.value.trim().toLowerCase();
    let any = false;
    for (const box of groups) {
      let shown = 0;
      for (const link of box.querySelectorAll("a")) {
        const hit = !q || link.textContent.toLowerCase().includes(q);
        link.hidden = !hit;
        if (hit) shown++;
      }
      box.hidden = shown === 0;
      any ||= shown > 0;
    }
    empty.hidden = any;
  });
}

// ---- headings, "On this page" and previous/next --------------------------------------------------

function slug(text) {
  return text.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "");
}

function buildToc(article, toc) {
  const headings = [...article.querySelectorAll("h2, h3")].filter(h => !h.closest(".card"));
  const titles = headings.map(h => h.textContent.trim());
  const used = new Set();
  for (const h of headings) {
    if (!h.id) {
      let id = slug(h.textContent), n = 2;
      while (used.has(id) || document.getElementById(id)) id = slug(h.textContent) + "-" + n++;
      h.id = id;
    }
    used.add(h.id);
    h.append(el("a", { class: "anchor", href: "#" + h.id, "aria-label": "Link to this section", text: "#" }));
  }
  if (headings.filter(h => h.tagName === "H2").length < 2) { toc.remove(); return; }

  toc.append(el("h4", { text: "On this page" }));
  const links = headings.map((h, i) => {
    const link = el("a", { href: "#" + h.id, text: titles[i] });
    if (h.tagName === "H3") link.classList.add("sub");
    toc.append(link);
    return link;
  });

  const mark = () => {
    let current = 0;
    headings.forEach((h, i) => { if (h.getBoundingClientRect().top < 120) current = i; });
    links.forEach((l, i) => l.classList.toggle("current", i === current));
  };
  document.addEventListener("scroll", mark, { passive: true });
  mark();
}

function buildPager(article) {
  const flat = NAV.flatMap(g => g.pages);
  const i = flat.findIndex(([file]) => file === currentFile);
  if (i < 0) return;
  const pager = el("nav", { class: "pager", "aria-label": "Previous and next page" });
  if (i > 0) {
    const [file, title] = flat[i - 1];
    pager.append(el("a", { class: "prev", href: file }, [el("span", { text: "← Previous" }), title]));
  }
  if (i < flat.length - 1) {
    const [file, title] = flat[i + 1];
    pager.append(el("a", { class: "next", href: file }, [el("span", { text: "Next →" }), title]));
  }
  article.append(pager);
  article.append(el("p", {
    class: "site-footer",
    text: "Empire Engine 0.1.5 documentation. Empire Engine is open source under the Apache License 2.0; these pages live in the repository's docs/ folder.",
  }));
}

// ---- code blocks ----------------------------------------------------------------------------------

const CS_KEYWORDS = new Set((
  "abstract as base bool break byte case catch char class const continue decimal default do double else enum " +
  "false finally float for foreach get if in init int interface internal is long namespace new null object out " +
  "override private protected public readonly record ref required return sealed set short static string struct " +
  "switch this throw true try typeof uint ulong using var virtual void when where while yield async await nameof"
).split(" "));

function escapeHtml(s) {
  return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}

function highlightCs(source) {
  const token = /(\/\/[^\n]*)|(\$?@?"(?:\\.|[^"\\\n])*"|'(?:\\.|[^'\\\n])')|(\b\d+(?:\.\d+)?[fFdDmM]?\b)|([A-Za-z_][A-Za-z0-9_]*)/g;
  let out = "", last = 0, m;
  while ((m = token.exec(source))) {
    out += escapeHtml(source.slice(last, m.index));
    const [text, comment, str, num, word] = m;
    let cls = null;
    if (comment) cls = "com";
    else if (str) cls = "str";
    else if (num) cls = "num";
    else if (word) {
      if (CS_KEYWORDS.has(word)) cls = "kw";
      else if (source[token.lastIndex] === "(") cls = "fn";
      else if (/^[A-Z]/.test(word)) cls = "type";
    }
    out += cls ? `<span class="tok-${cls}">${escapeHtml(text)}</span>` : escapeHtml(text);
    last = token.lastIndex;
  }
  return out + escapeHtml(source.slice(last));
}

function highlightJson(source) {
  const token = /("(?:\\.|[^"\\])*")(\s*:)?|(-?\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b)|\b(true|false|null)\b/g;
  let out = "", last = 0, m;
  while ((m = token.exec(source))) {
    out += escapeHtml(source.slice(last, m.index));
    const [text, str, colon, num, lit] = m;
    if (str) out += `<span class="tok-${colon ? "type" : "str"}">${escapeHtml(str)}</span>${colon || ""}`;
    else if (num) out += `<span class="tok-num">${text}</span>`;
    else if (lit) out += `<span class="tok-kw">${text}</span>`;
    last = token.lastIndex;
  }
  return out + escapeHtml(source.slice(last));
}

function highlightShell(source) {
  return source.split("\n").map(line =>
    /^\s*#/.test(line) ? `<span class="tok-com">${escapeHtml(line)}</span>` : escapeHtml(line)
  ).join("\n");
}

function decorateCode(article) {
  for (const pre of article.querySelectorAll("pre")) {
    const code = pre.querySelector("code") || pre;
    const lang = (code.className.match(/language-(\w+)/) || [])[1];
    const text = code.textContent.replace(/^\n/, "").replace(/\s+$/, "");
    if (lang === "cs") code.innerHTML = highlightCs(text);
    else if (lang === "json") code.innerHTML = highlightJson(text);
    else if (lang === "sh") code.innerHTML = highlightShell(text);
    else code.textContent = text;

    const wrap = el("div", { class: "code" });
    pre.replaceWith(wrap);
    if (pre.dataset.label) wrap.append(el("div", { class: "code-label", text: pre.dataset.label }));
    wrap.append(pre);

    const copy = el("button", { class: "copy-btn", type: "button", text: "Copy" });
    copy.addEventListener("click", async () => {
      try {
        await navigator.clipboard.writeText(text);
        copy.textContent = "Copied";
      } catch {
        copy.textContent = "Select and copy";
      }
      setTimeout(() => { copy.textContent = "Copy"; }, 1400);
    });
    wrap.append(copy);
  }
}

// Wide tables scroll sideways on their own instead of widening the page.
function wrapTables(article) {
  for (const table of article.querySelectorAll("table")) {
    if (table.parentElement.classList.contains("table-wrap")) continue;
    const wrap = el("div", { class: "table-wrap" });
    table.replaceWith(wrap);
    wrap.append(table);
  }
}

// ---- page -----------------------------------------------------------------------------------------

document.addEventListener("DOMContentLoaded", () => {
  const article = document.querySelector("article");
  document.body.prepend(buildTopbar());

  const layout = el("div", { class: "layout" });
  const nav = el("nav", { id: "sidebar", "aria-label": "Documentation pages" });
  const main = el("main", { id: "content" });
  const toc = el("aside", { id: "toc", "aria-label": "On this page" });
  article.replaceWith(layout);
  main.append(article);
  layout.append(nav, main, toc);

  buildSidebar(nav);
  decorateCode(article);
  wrapTables(article);
  buildToc(article, toc);
  buildPager(article);

  // On phones the page list slides over the page; following a link or tapping the page closes it.
  main.addEventListener("click", () => document.body.classList.remove("nav-open"));
  nav.addEventListener("click", e => { if (e.target.tagName === "A") document.body.classList.remove("nav-open"); });
});
