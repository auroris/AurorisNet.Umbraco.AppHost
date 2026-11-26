import { memberSignInPreview } from "./member-sign-in-content.js";
import { childPageLinksPreview } from "./child-page-links-content.js";
import { nestedBlockPreviews, previewStylesheets } from "../ApplicationBlocks/block-preview.js";
const pathBase = new URL(import.meta.url).pathname.split("/App_Plugins/")[0];
export function onInit(_host, registry) {
    nestedBlockPreviews.set("appMemberSignIn", memberSignInPreview);
    nestedBlockPreviews.set("appChildPageLinks", childPageLinksPreview);
    const themeStyles = registry?.getByAlias("UmbracoAppHost.PreviewThemeStyles")?.meta?.stylesheets;
    if (Array.isArray(themeStyles) && themeStyles.length > 0) {
        previewStylesheets[1] = themeStyles[0];
        for (let i = 1; i < themeStyles.length; i++) {
            if (!previewStylesheets.includes(themeStyles[i])) previewStylesheets.push(themeStyles[i]);
        }
    } else {
        // A consuming theme can replace this slot with its own existing frontend bundle.
        previewStylesheets[1] ??= `${pathBase}/dist/css/site.css`;
    }
}
