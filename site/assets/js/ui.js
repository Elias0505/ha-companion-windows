/* SPDX-License-Identifier: AGPL-3.0-only
   Page plumbing: theme toggle, sticky-header state, entrance and scroll
   reveals, copy-to-clipboard, the hero video (loaded late, never autoplaying
   where it would be rude), and the screenshot lightbox. */
(function () {
  "use strict";

  var doc = document.documentElement;
  var reduced = doc.classList.contains("rm");

  /* ---------- theme toggle ------------------------------------------------ */

  (function () {
    var btn = document.querySelector("[data-theme-toggle]");
    if (!btn) return;

    function current() {
      return doc.dataset.theme ||
        (matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark");
    }
    function sync() {
      var mode = current();
      btn.setAttribute("aria-label", "Switch to " + (mode === "dark" ? "light" : "dark") + " theme");
      // the browser chrome colour must follow the toggle, not only the OS
      document.querySelectorAll('meta[name="theme-color"]').forEach(function (m) {
        m.setAttribute("content", mode === "dark" ? "#171717" : "#ffffff");
        m.removeAttribute("media");
      });
    }
    if (doc.dataset.theme) sync();   // untouched OS default: leave the media-attr metas alone
    btn.addEventListener("click", function () {
      var next = current() === "dark" ? "light" : "dark";
      doc.dataset.theme = next;
      try { localStorage.setItem("hac-theme", next); } catch (e) { /* private mode */ }
      sync();
    });
    matchMedia("(prefers-color-scheme: light)").addEventListener("change", function () {
      if (!doc.dataset.theme) btn.setAttribute("aria-label",
        "Switch to " + (current() === "dark" ? "light" : "dark") + " theme");
    });
  })();

  /* ---------- sticky header gets a hairline once you leave the top -------- */

  (function () {
    var header = document.getElementById("site-header");
    if (!header || !("IntersectionObserver" in window)) return;
    var sentinel = document.createElement("div");
    sentinel.setAttribute("aria-hidden", "true");
    sentinel.style.cssText = "position:absolute;top:0;height:1px;width:1px";
    document.body.prepend(sentinel);
    new IntersectionObserver(function (entries) {
      header.classList.toggle("is-stuck", !entries[0].isIntersecting);
    }).observe(sentinel);
  })();

  /* ---------- hero entrance ----------------------------------------------- */

  (function () {
    var hero = document.querySelector(".hero");
    if (!hero) return;

    // The "before" state lives in CSS behind html.js — never in inline styles.
    // If this script ever throws before the class lands, the fallback timer
    // still reveals everything: an invisible hero is worse than no animation.
    function ready() { hero.classList.add("is-ready"); }
    if (reduced) { ready(); return; }

    requestAnimationFrame(function () { requestAnimationFrame(ready); });
    window.setTimeout(ready, 1200);

    // will-change is a promise to the compositor, not a decoration — take it
    // back once the entrance has played.
    hero.addEventListener("transitionend", function (e) {
      if (e.target.hasAttribute("data-hero")) e.target.style.willChange = "auto";
    });
  })();

  /* ---------- scroll reveals ---------------------------------------------- */

  (function () {
    var targets = document.querySelectorAll(".reveal");
    if (!targets.length) return;
    if (reduced || !("IntersectionObserver" in window)) {
      targets.forEach(function (el) { el.classList.add("is-in"); });
      return;
    }
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (!entry.isIntersecting) return;
        entry.target.classList.add("is-in");
        io.unobserve(entry.target);
      });
    }, { rootMargin: "0px 0px -12% 0px", threshold: 0.08 });
    targets.forEach(function (el) { io.observe(el); });
  })();

  /* ---------- copy to clipboard ------------------------------------------- */

  (function () {
    var status = document.querySelector("[data-copy-status]");

    function announce(msg) { if (status) status.textContent = msg; }

    document.querySelectorAll("[data-copy]").forEach(function (btn) {
      var label = btn.querySelector(".copy__label");
      btn.addEventListener("click", function () {
        var code = document.querySelector(btn.getAttribute("data-copy"));
        if (!code) return;
        var text = code.textContent.trim();

        function ok() {
          btn.classList.add("is-done");
          if (label) label.textContent = "Copied";
          announce("Install command copied to clipboard.");
          window.clearTimeout(btn._resetTimer);
          btn._resetTimer = window.setTimeout(function () {
            btn.classList.remove("is-done");
            if (label) label.textContent = "Copy";
          }, 1800);
        }
        function fallback() {
          // insecure context (file://) or a rejected permission: select the text
          // so Ctrl+C works, and never claim a success that did not happen
          var range = document.createRange();
          range.selectNodeContents(code);
          var sel = window.getSelection();
          sel.removeAllRanges();
          sel.addRange(range);
          if (label) label.textContent = "Press Ctrl+C";
          announce("Press Control C to copy the selected command.");
          window.setTimeout(function () { if (label) label.textContent = "Copy"; }, 3000);
        }

        if (navigator.clipboard && window.isSecureContext) {
          navigator.clipboard.writeText(text).then(ok, fallback);
        } else {
          fallback();
        }
      });
    });
  })();

  /* ---------- hero video --------------------------------------------------- */

  (function () {
    var figure = document.querySelector(".hero__media");
    if (!figure) return;
    var frame = figure.querySelector(".frame");
    var poster = document.getElementById("hero-poster");
    var playBtn = figure.querySelector("[data-hero-play]");
    var video = null;

    var conn = navigator.connection || {};
    var saveData = conn.saveData === true;
    var slow = /(^|-)2g$/.test(conn.effectiveType || "");
    var small = window.matchMedia("(max-width: 767px)").matches;

    function build(withControls) {
      video = document.createElement("video");
      video.muted = true;
      video.loop = true;
      video.playsInline = true;
      video.setAttribute("playsinline", "");
      video.setAttribute("disablepictureinpicture", "");
      video.preload = "auto";
      video.setAttribute("aria-hidden", "true");
      video.tabIndex = -1;
      if (withControls) { video.controls = true; video.removeAttribute("aria-hidden"); video.tabIndex = 0; }

      ["assets/video/quick-panel-demo.webm", "assets/video/quick-panel-demo.mp4"].forEach(function (src) {
        var s = document.createElement("source");
        s.src = src;
        s.type = src.slice(-4) === "webm" ? "video/webm" : "video/mp4";
        video.appendChild(s);
      });

      video.addEventListener("playing", function () {
        video.classList.add("is-playing");
        if (poster) poster.classList.add("is-hidden");
        if (playBtn) playBtn.hidden = true;
      });
      frame.appendChild(video);
      return video;
    }

    function offerButton() {
      if (!playBtn) return;
      playBtn.hidden = false;
      // no { once }: if play() rejects, the button must keep working
      playBtn.addEventListener("click", function () {
        if (video && !video.paused) return;
        var v = build(true);
        v.play().then(function () {
          v.classList.add("is-playing");
        }, function () {
          // source unplayable: remove the dead element, leave the button alive
          v.remove();
          video = null;
        });
      });
    }

    // Never the initial DOM: the poster is the LCP element and must not compete
    // with a video download. And a frozen first frame reads as a broken page,
    // so anything that could refuse to autoplay gets the honest button instead.
    if (reduced || small || saveData || slow || !("IntersectionObserver" in window)) {
      offerButton();
      return;
    }

    window.addEventListener("load", function () {
      var io = new IntersectionObserver(function (entries) {
        if (!entries[0].isIntersecting) return;
        io.disconnect();
        var v = build(false);
        var p = v.play();
        if (p && typeof p.catch === "function") {
          p.catch(function () { v.remove(); video = null; offerButton(); });
        }
      }, { threshold: 0.25 });
      io.observe(figure);
    });
  })();

  /* ---------- lightbox ----------------------------------------------------- */

  (function () {
    var dialog = document.querySelector("[data-lightbox-dialog]");
    if (!dialog || typeof dialog.showModal !== "function") {
      // no <dialog> support: let the buttons fall back to nothing rather than
      // opening a half-broken overlay
      document.querySelectorAll("[data-lightbox]").forEach(function (b) { b.hidden = true; });
      return;
    }

    var img = dialog.querySelector("[data-lightbox-img]");
    var caption = dialog.querySelector("[data-lightbox-caption]");
    var triggers = Array.prototype.slice.call(document.querySelectorAll("[data-lightbox]"));
    var index = 0;
    var opener = null;

    var supportsAvif = null;
    function avifOk() {
      if (supportsAvif !== null) return Promise.resolve(supportsAvif);
      return new Promise(function (resolve) {
        var probe = new Image();
        probe.onload = probe.onerror = function () {
          supportsAvif = probe.width > 0 && probe.height > 0;
          resolve(supportsAvif);
        };
        probe.src = "data:image/avif;base64,AAAAIGZ0eXBhdmlmAAAAAGF2aWZtaWYxbWlhZk1BMUIAAADybWV0YQAAAAAAAAAoaGRscgAAAAAAAAAAcGljdAAAAAAAAAAAAAAAAGxpYmF2aWYAAAAADnBpdG0AAAAAAAEAAAAeaWxvYwAAAABEAAABAAEAAAABAAABGgAAAB0AAAAoaWluZgAAAAAAAQAAABppbmZlAgAAAAABAABhdjAxQ29sb3IAAAAAamlwcnAAAABLaXBjbwAAABRpc3BlAAAAAAAAAAEAAAABAAAAEHBpeGkAAAAAAwgICAAAAAxhdjFDgQAMAAAAABNjb2xybmNseAACAAIABoAAAAAXaXBtYQAAAAAAAAABAAEEAQKDBAAAACVtZGF0EgAKCBgABogQEDQgMgkQAAAAB8dSLfI=";
      });
    }

    function show(i) {
      index = (i + triggers.length) % triggers.length;
      var btn = triggers[index];
      var avif = btn.getAttribute("data-lightbox");
      var webp = btn.getAttribute("data-lightbox-fallback");
      var title = btn.getAttribute("data-lightbox-title") || "";
      var figureImg = btn.closest("figure") ? btn.closest("figure").querySelector("img") : null;

      avifOk().then(function (ok) {
        if (btn !== triggers[index]) return;   // user already moved on
        img.src = ok ? avif : (webp || avif);
      });
      img.alt = figureImg ? figureImg.alt : title;
      if (caption) caption.textContent = title;
    }

    triggers.forEach(function (btn, i) {
      btn.addEventListener("click", function () {
        opener = btn;
        show(i);
        dialog.showModal();
      });
    });

    dialog.querySelector("[data-lightbox-close]").addEventListener("click", function () { dialog.close(); });
    dialog.querySelector("[data-lightbox-prev]").addEventListener("click", function () { show(index - 1); });
    dialog.querySelector("[data-lightbox-next]").addEventListener("click", function () { show(index + 1); });
    dialog.addEventListener("keydown", function (e) {
      if (e.key === "ArrowLeft") { show(index - 1); e.preventDefault(); }
      if (e.key === "ArrowRight") { show(index + 1); e.preventDefault(); }
    });
    dialog.addEventListener("click", function (e) {
      // click outside the picture closes, like every image viewer ever
      if (e.target === dialog) dialog.close();
    });
    dialog.addEventListener("close", function () {
      if (opener) { opener.focus(); opener = null; }
    });
  })();
})();
