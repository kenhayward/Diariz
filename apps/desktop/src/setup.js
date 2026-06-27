"use strict";

const input = document.getElementById("url");
const connect = document.getElementById("connect");
const error = document.getElementById("error");

// Pre-fill with the currently-configured address (when changing it from Settings).
window.setup.getCurrent().then((cur) => {
  if (cur) input.value = cur;
  input.focus();
  input.select();
});

async function submit() {
  error.textContent = "";
  connect.disabled = true;
  connect.textContent = "Connecting…";
  try {
    const result = await window.setup.save(input.value);
    if (!result.ok) {
      error.textContent = result.error || "Could not connect.";
    }
    // On success the main process closes this window and opens the app.
  } finally {
    connect.disabled = false;
    connect.textContent = "Connect";
  }
}

connect.addEventListener("click", submit);
input.addEventListener("keydown", (e) => {
  if (e.key === "Enter") submit();
});
