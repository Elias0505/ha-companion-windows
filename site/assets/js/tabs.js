/* SPDX-License-Identifier: AGPL-3.0-only
   The ARIA tabs pattern, once, for both tab strips on the page.

   Two panel styles are supported by the same widget:
   - stacked (`.tabs__panels--stacked`): all panels are layered in one box so
     they can crossfade. Inactive ones get `inert` + `aria-hidden` — `hidden`
     would kill the transition, and opacity alone would leave them in the tab
     order and readable by screen readers.
   - plain: only one panel is in flow at a time, toggled with `hidden`.

   Classic script, no modules: the page must also work from file://. */
(function () {
  "use strict";

  var supportsInert = "inert" in HTMLElement.prototype;

  function init(root) {
    var list = root.querySelector('[role="tablist"]');
    if (!list) return;

    var tabs = Array.prototype.slice.call(list.querySelectorAll('[role="tab"]'));
    var panels = tabs.map(function (t) { return document.getElementById(t.getAttribute("aria-controls")); });
    var ink = list.querySelector(".tabs__ink");
    var stacked = !!root.querySelector(".tabs__panels--stacked");

    function moveInk(tab) {
      if (!ink) return;
      // Position via transform, length via real width. Scaling a 1px bar would
      // also scale its 2px corner radius a hundredfold - the ends taper into a
      // long lens and the bar looks misaligned. A width transition on a 2px-tall
      // element costs nothing. (The bar lives inside the scroller, so it scrolls
      // with the tabs by itself.)
      ink.style.width = tab.offsetWidth + "px";
      ink.style.transform = "translateX(" + tab.offsetLeft + "px)";
    }

    function select(index, focusTab) {
      tabs.forEach(function (tab, i) {
        var on = i === index;
        var panel = panels[i];
        tab.setAttribute("aria-selected", on ? "true" : "false");
        tab.tabIndex = on ? 0 : -1;
        if (!panel) return;

        if (stacked) {
          panel.classList.toggle("is-active", on);
          if (on) {
            panel.removeAttribute("aria-hidden");
            if (supportsInert) panel.inert = false; else panel.removeAttribute("hidden");
          } else {
            panel.setAttribute("aria-hidden", "true");
            if (supportsInert) panel.inert = true; else panel.setAttribute("hidden", "");
          }
        } else {
          panel.hidden = !on;
          panel.classList.toggle("is-active", on);
        }
      });

      moveInk(tabs[index]);
      if (focusTab) {
        tabs[index].focus();
        tabs[index].scrollIntoView({ block: "nearest", inline: "nearest" });
      }
    }

    tabs.forEach(function (tab, i) {
      tab.addEventListener("click", function () { select(i, false); });
      tab.addEventListener("keydown", function (e) {
        var next = null;
        if (e.key === "ArrowRight") next = (i + 1) % tabs.length;
        else if (e.key === "ArrowLeft") next = (i - 1 + tabs.length) % tabs.length;
        else if (e.key === "Home") next = 0;
        else if (e.key === "End") next = tabs.length - 1;
        if (next === null) return;
        e.preventDefault();
        select(next, true);   // automatic activation: panels are pre-rendered
      });
    });

    // the ink bar is measured, so it has to be re-measured when things move
    var active = function () {
      var i = tabs.findIndex(function (t) { return t.getAttribute("aria-selected") === "true"; });
      return tabs[i < 0 ? 0 : i];
    };
    if (window.ResizeObserver) {
      new ResizeObserver(function () {
        requestAnimationFrame(function () { moveInk(active()); });
      }).observe(list);
    }
    // Tab widths change when Inter arrives (font-display: swap) and the
    // ResizeObserver above never fires for that — the list's own box is stable.
    if (document.fonts && document.fonts.ready) {
      document.fonts.ready.then(function () { moveInk(active()); });
    }

    // initial state, after layout so offsetLeft/Width are real
    requestAnimationFrame(function () {
      select(Math.max(0, tabs.indexOf(active())), false);
    });
  }

  document.querySelectorAll("[data-tabs]").forEach(init);
})();
