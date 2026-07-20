/** Side panel entry (esbuild `sidepanel/sidepanel`). Mounts the Svelte app. */
import { mount } from "svelte";
import App from "./App.svelte";

const target = document.getElementById("app");
if (target) {
  mount(App, { target });
} else {
  console.error("[sidepanel] #app root not found");
}
