import { BlockPreview, nestedBlockPreviews } from "../ApplicationBlocks/block-preview.js";

const preview = () => '<button type="button">Find person</button><p class="preview-note">Searches the configured directory scope and fills the containing form’s identity, name and mapped fields.</p>';
export function onInit() { nestedBlockPreviews.set("44e68f89-6ea4-706e-5916-89cd86c0d32a", preview); }
export default class PeoplePreview extends BlockPreview { renderContent() { return preview(); } }
customElements.define("app-people-preview", PeoplePreview);
