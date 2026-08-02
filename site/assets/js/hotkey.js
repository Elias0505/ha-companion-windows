/* SPDX-License-Identifier: AGPL-3.0-only
   "Try it right here": the page's one interactive moment, mirroring what the
   app does when the global hotkey fires.

   Accessibility note that drives the whole design: a single-character shortcut
   (`h`) is a WCAG 2.1.4 violation when it listens on the document. Here the
   listener sits on the widget itself, so it only fires while focus is inside —
   the "active only on focus" exemption — and it bails on any modifier key or
   editable target. Escape is handled the same way. */
(function () {
  "use strict";

  var root = document.querySelector("[data-hotkey]");
  if (!root) return;

  var keys = root.querySelector(".hk__keys");
  var panel = root.querySelector(".hk__panel");
  var toast = root.querySelector(".hk__toast");
  var live = root.querySelector("[data-hk-live]");
  var reduced = document.documentElement.classList.contains("rm");
  var toastTimer = null;   // hide-again timer
  var toastDelay = null;   // show-after-landing timer
  var userTouched = false; // suppresses the auto-demo once the visitor acted
  var open = false;

  function setState(next, announce) {
    if (next === open) return;
    open = next;
    if (announce) userTouched = true;
    root.dataset.state = open ? "open" : "closed";
    if (keys) keys.setAttribute("aria-pressed", open ? "true" : "false");
    if (announce && live) live.textContent = open ? "Quick panel open." : "Quick panel closed.";

    // both toast timers die on every state change, or a fast close/open would
    // resurrect the previous cycle's toast
    window.clearTimeout(toastTimer);
    window.clearTimeout(toastDelay);
    if (!open) {
      if (toast) toast.classList.remove("is-in");
      return;
    }
    // The toast belongs after the panel has landed, not on a guessed timer —
    // unless motion is reduced, where there is no transition to wait for.
    if (!toast || !panel) return;
    if (reduced) {
      showToast(4000);
    } else {
      var done = function () {
        panel.removeEventListener("transitionend", done);
        panel.removeEventListener("transitioncancel", done);
        if (open) showToast(2200);
      };
      panel.addEventListener("transitionend", done);
      panel.addEventListener("transitioncancel", done);
    }
  }

  function showToast(ms) {
    toastDelay = window.setTimeout(function () {
      if (!open) return;
      toast.classList.add("is-in");
      toastTimer = window.setTimeout(function () { toast.classList.remove("is-in"); }, ms);
    }, reduced ? 0 : 140);
  }

  if (keys) keys.addEventListener("click", function () { setState(!open, true); });

  root.addEventListener("keydown", function (e) {
    if (e.altKey || e.ctrlKey || e.metaKey) return;
    if (e.target.closest("input, textarea, select, [contenteditable]")) return;
    if (e.key === "Escape") {
      if (open) { setState(false, true); e.preventDefault(); }
      return;
    }
    if (e.key === "h" || e.key === "H") {
      // let the button's own Enter/Space handling do its job untouched
      setState(!open, true);
      e.preventDefault();
    }
  });

  // Show it once when the section comes into view, so a visitor who never
  // touches the keyboard still sees what the app does. It never auto-closes and
  // never repeats, and it does not talk to the live region (no screen-reader
  // noise for something the user did not do).
  if (!reduced && "IntersectionObserver" in window) {
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (!entry.isIntersecting) return;
        io.disconnect();
        window.setTimeout(function () {
          if (!userTouched && !open) setState(true, false);
        }, 600);
      });
    }, { threshold: 0.3 });   // 0.5 is unreachable when the widget is taller than the viewport
    io.observe(root);
  }

  // Touch: swipe in from the right edge, swipe out again. Pointer events only,
  // and `touch-action: pan-y` on the stage keeps vertical scrolling intact.
  var stage = root.querySelector(".hk__stage");
  if (stage && window.PointerEvent) {
    var startX = 0, startY = 0, tracking = false;
    stage.addEventListener("pointerdown", function (e) {
      if (e.pointerType === "mouse") return;
      startX = e.clientX; startY = e.clientY;
      var rect = stage.getBoundingClientRect();
      tracking = open || (e.clientX - rect.left) / rect.width > 0.72;
    }, { passive: true });
    stage.addEventListener("pointermove", function (e) {
      if (!tracking) return;
      var dx = e.clientX - startX;
      if (Math.abs(e.clientY - startY) > Math.abs(dx)) return; // vertical scroll wins
      if (dx < -24 && !open) { setState(true, true); tracking = false; }
      else if (dx > 24 && open) { setState(false, true); tracking = false; }
    }, { passive: true });
    stage.addEventListener("pointerup", function () { tracking = false; }, { passive: true });
    stage.addEventListener("pointercancel", function () { tracking = false; }, { passive: true });
  }
})();
