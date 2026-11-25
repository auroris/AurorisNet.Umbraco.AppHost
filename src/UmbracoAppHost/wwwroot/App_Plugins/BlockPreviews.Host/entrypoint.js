import { memberSignInPreview } from "./member-sign-in.js";
import { nestedBlockPreviews, previewStylesheets } from "../ApplicationBlocks/block-preview.js";
const pathBase = new URL(import.meta.url).pathname.split("/App_Plugins/")[0];
export function onInit() {
    nestedBlockPreviews.set("eb0dc9f3-5ad2-f3d8-876e-0788d62fce84", memberSignInPreview);
    // A consuming theme can replace this slot with its own existing frontend bundle.
    previewStylesheets[1] ??= `${pathBase}/dist/css/site.css`;
}
