import { BlockPreview } from "../ApplicationBlocks/block-preview.js";
import { childPageLinksPreview } from "./child-page-links-content.js";
export { childPageLinksPreview };

export default class ChildPageLinksPreview extends BlockPreview {
    renderContent(alias, content) { return childPageLinksPreview(content); }
}
customElements.define("app-child-page-links-preview", ChildPageLinksPreview);
