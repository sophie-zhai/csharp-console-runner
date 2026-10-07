(() => {
  "use strict";

  const starter = `using System;

public class Program
{
    public static void Main()
    {
        Console.WriteLine("Hello, world!");
    }
}`;

  const code = document.getElementById("code");
  const runButton = document.getElementById("runButton");
  const resetButton = document.getElementById("resetButton");
  const output = document.getElementById("output");
  const status = document.getElementById("status");

  function apiBase() {
    const value = String(window.CSHARP_RUNNER_API || "").trim().replace(/\/$/, "");
    if (!value || value.includes("__RUNNER_API_URL__")) return null;
    return value;
  }

  function show(text) {
    output.textContent = text;
  }

  function setBusy(busy) {
    runButton.disabled = busy;
    resetButton.disabled = busy;
    code.readOnly = busy;
    status.textContent = busy ? "Running..." : "";
  }

  async function run() {
    const base = apiBase();
    if (!base) {
      show("Runner API is not configured.");
      return;
    }

    if (!code.value.trim()) {
      show("Enter a C# program first.");
      return;
    }

    setBusy(true);
    show("Running...");

    try {
      const response = await fetch(`${base}/api/run`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ code: code.value })
      });

      let result;
      try { result = await response.json(); }
      catch { result = null; }

      if (!response.ok || !result) {
        show("The runner is unavailable. Please try again.");
        return;
      }

      switch (result.status) {
        case "success":
          show(result.output || "Program completed with no output.");
          break;
        case "compile_error":
          show("Compilation failed.\n\nReview your code and try again.");
          break;
        case "runtime_error":
          show("Program terminated with an error.");
          break;
        case "timeout":
          show("Program stopped because it exceeded the execution time limit.");
          break;
        default:
          show("The program could not be run.");
      }
    } catch {
      show("The runner is unavailable. Please try again.");
    } finally {
      setBusy(false);
    }
  }

  code.addEventListener("keydown", (event) => {
    if (event.key === "Tab") {
      event.preventDefault();
      const start = code.selectionStart;
      const end = code.selectionEnd;
      code.setRangeText("    ", start, end, "end");
    }
  });

  runButton.addEventListener("click", run);
  resetButton.addEventListener("click", () => {
    code.value = starter;
    show("Program output will appear here.");
    code.focus();
  });
})();
