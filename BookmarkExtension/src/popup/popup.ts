/**
 * Popup entry (esbuild `popup/popup`). Logic lives in `popup-controller.ts`;
 * DOM wiring in `popup-ui.ts`.
 */
export {
  DEFAULT_API_BASE_URL,
  PopupController,
  withRecentApiBaseUrl,
  type PopupBookmarkApi,
  type PopupDeps,
} from "./popup-controller";

import "./popup-ui";
