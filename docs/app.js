"use strict";
const copy = window.MULTIJANELA_I18N;
const screens = {
  accounts: {src: "overview.png", en: "Your account library and running clients share a home, with separate lists and independent scrolling.", pt: "Sua biblioteca de contas e os clientes abertos na mesma tela, com listas separadas e rolagem independente."},
  launch: {src: "launch.png", en: "Build your own account sequence, reorder it and launch into a saved destination.", pt: "Monte sua sequência de contas, altere a ordem e abra em um destino salvo."},
  cpu: {src: "cpu.png", en: "Save an individual CPU limit or inherit the global value, even while an account is closed.", pt: "Salve um limite de CPU individual ou herde o valor global, mesmo com a conta fechada."},
  versions: {src: "versions.png", en: "Explore published Roblox clients, compare versions and download from the official CDN.", pt: "Explore clientes publicados do Roblox, compare versões e baixe pela CDN oficial."},
  settings: {src: "settings.png", en: "Choose a theme, readable text and your language. Preferences apply and save automatically.", pt: "Escolha o tema, o tamanho do texto e o idioma. Preferências são aplicadas e salvas automaticamente."}
};
let language = "en", screen = "accounts";
try { language = localStorage.getItem("multijanela.site.language") === "pt" ? "pt" : "en"; } catch {}
function updateScreen(key, focus = false) {
  if (!screens[key]) return;
  screen = key;
  const item = screens[key], image = document.querySelector("#gallery-image");
  image.src = "assets/" + item.src; image.alt = item[language];
  document.querySelector("#gallery-caption").textContent = item[language];
  document.querySelector("#gallery-panel").setAttribute("aria-labelledby", "tab-" + key);
  document.querySelectorAll("[data-screen]").forEach(button => {
    const selected = button.dataset.screen === key;
    button.setAttribute("aria-selected", String(selected)); button.tabIndex = selected ? 0 : -1;
    if (selected && focus) button.focus();
  });
}
function setLanguage(next) {
  language = next === "pt" ? "pt" : "en";
  document.documentElement.lang = language === "pt" ? "pt-BR" : "en";
  document.querySelectorAll("[data-i18n]").forEach(node => {
    const value = copy[language][node.dataset.i18n];
    if (value !== undefined) { node.textContent = value; if (node.dataset.i18n === "downloadTitle") node.style.whiteSpace = "pre-line"; }
  });
  document.querySelectorAll("[data-i18n-alt]").forEach(node => node.alt = copy[language][node.dataset.i18nAlt]);
  document.querySelectorAll("[data-lang]").forEach(node => node.setAttribute("aria-pressed", String(node.dataset.lang === language)));
  document.querySelector("#close-preview").setAttribute("aria-label", language === "pt" ? "Fechar imagem" : "Close preview");
  document.querySelector(".header nav").setAttribute("aria-label", language === "pt" ? "Navegação principal" : "Main navigation");
  document.querySelector(".language").setAttribute("aria-label", language === "pt" ? "Idioma" : "Language");
  document.querySelector(".gallery-tabs").setAttribute("aria-label", language === "pt" ? "Imagens do aplicativo" : "App screenshots");
  document.querySelector(".hero-screen").setAttribute("aria-label", language === "pt" ? "Conhecer as telas do MultiJanela" : "Explore MultiJanela screenshots");
  document.querySelector(".download-art img").src = "assets/download-windows" + (language === "pt" ? "-pt" : "") + ".svg";
  document.querySelector(".header .brand").setAttribute("aria-label", language === "pt" ? "MultiJanela início" : "MultiJanela home");
  document.title = language === "pt" ? "MultiJanela — Suas contas. Tudo no seu lugar." : "MultiJanela — Your accounts. One calm workspace.";
  try { localStorage.setItem("multijanela.site.language", language); } catch {}
  updateScreen(screen);
}
document.querySelectorAll("[data-lang]").forEach(node => node.addEventListener("click", () => setLanguage(node.dataset.lang)));
document.querySelectorAll("[data-screen]").forEach(node => node.addEventListener("click", () => updateScreen(node.dataset.screen)));
document.querySelector(".gallery-tabs").addEventListener("keydown", event => {
  const keys = Object.keys(screens); let index = keys.indexOf(screen);
  if (event.key === "ArrowRight") index = (index + 1) % keys.length;
  else if (event.key === "ArrowLeft") index = (index - 1 + keys.length) % keys.length;
  else if (event.key === "Home") index = 0; else if (event.key === "End") index = keys.length - 1; else return;
  event.preventDefault(); updateScreen(keys[index], true);
});
const dialog = document.querySelector("#preview-dialog");
document.querySelector("#zoom-button").addEventListener("click", () => {
  const image = document.querySelector("#gallery-image");
  document.querySelector("#dialog-image").src = image.src; document.querySelector("#dialog-image").alt = image.alt;
  document.querySelector("#dialog-caption").textContent = screens[screen][language]; dialog.showModal();
});
document.querySelector("#close-preview").addEventListener("click", () => dialog.close());
dialog.addEventListener("click", event => {
  if (event.target !== dialog) return;
  const box = dialog.getBoundingClientRect();
  if (event.clientX < box.left || event.clientX > box.right || event.clientY < box.top || event.clientY > box.bottom) dialog.close();
});
setLanguage(language);
if ("IntersectionObserver" in window && !matchMedia("(prefers-reduced-motion: reduce)").matches) {
  document.documentElement.classList.add("js");
  const observer = new IntersectionObserver(entries => entries.forEach(entry => {
    if (entry.isIntersecting) { entry.target.classList.add("visible"); observer.unobserve(entry.target); }
  }), {threshold: .07});
  document.querySelectorAll(".reveal").forEach(node => { node.classList.add("ready"); observer.observe(node); });
}
