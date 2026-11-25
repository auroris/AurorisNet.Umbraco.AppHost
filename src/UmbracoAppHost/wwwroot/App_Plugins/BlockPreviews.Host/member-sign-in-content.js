import { button, heading, note } from "../ApplicationBlocks/preview-model.js";

export function memberSignInPreview(content) {
    const automatic = [true, 1, "1", "true"].includes(content.automaticSignIn);
    return heading(content.heading || "Sign in")
        + '<label>Username<input disabled></label><label>Password<input type="password" disabled></label>'
        + button("Sign in")
        + note(automatic ? "Starts single sign-on for visitors who are not signed in as members." : "Visible to visitors who need to sign in as a member.");
}
