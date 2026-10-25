# Security and privacy

MultiJanela is unofficial and is not certified as compliant with Roblox policy. See [technical notes](docs/TECHNICAL.md) for account-moderation risks. This project does not promise to evade detection or prevent bans.

## Sensitive local data

`%LOCALAPPDATA%\RobloxMultiManager` contains account metadata, settings, saved destinations, logs and WebView2 profiles. Browser profiles contain authenticated session data. Private-server links may grant access to private servers. Treat the entire directory as private. Never upload it to an issue, repository, chat attachment or public report. Do not share passwords, `.ROBLOSECURITY` cookies, CSRF tokens, authorization tickets or recovery codes.

Local session repair backups under `%LOCALAPPDATA%\Roblox\AuthSessionBackups` also contain sensitive session data. Keep them private; never attach them to reports or commits. The repair does not upload or inspect their contents.

## Reporting

Use GitHub's private vulnerability reporting on this repository for sensitive security findings, if available. For an ordinary bug, open a public issue with a minimal redacted reproduction. If private reporting is unavailable, open an issue asking for a private contact without including exploit details or secrets. Do not test findings against other people's accounts or Roblox services without authorization. Account appeals belong with Roblox Support; this project cannot reverse moderation decisions.

## Downloads

The manager is unsigned. Download only from this repository's Releases page, review the source, and compare SHA256 values. Checksums alone are not proof of publisher identity. Do not disable antivirus, MFA or account protections. Do not treat the absence of a malware warning as evidence of Roblox approval.

## Português

Não publique a pasta de dados do MultiJanela, cookies, senhas, tickets, tokens, links privados ou relatórios sem revisão. Use relato privado de vulnerabilidade quando disponível. Problemas comuns podem ser relatados com dados ocultados. Moderação de conta deve ser tratada com o suporte Roblox. O executável não é assinado e não há garantia contra punições; não desative proteções do sistema ou da conta.


## Optional integrations and vault

Keep API/Nexus keys private and leave integrations disabled unless needed. Grant only the required actions. Review scripts before running; never paste passwords, cookies or private server links into shared code. A connected Nexus client is separate software and must be trusted independently.

The optional password vault protects manager JSON, not browser databases or logs. Protect the entire data directory and keep encrypted backups. Do not open vault data with an incompatible legacy manager. The vault password cannot be recovered by the project.
