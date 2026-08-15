# Security Policy

## Reporting a vulnerability

Please do not publish an exploitable vulnerability, credentials or private proxy configuration in a public GitHub issue.

For a suspected security problem, open a minimal report without secrets and clearly mark it as security-related, or use GitHub's private vulnerability reporting feature if it is enabled for the repository.

Include enough information to reproduce the issue safely:

- affected version/commit;
- Windows version;
- whether User Mode or Admin Mode is involved;
- reproduction steps;
- relevant logs with passwords, server addresses, subscription URLs, PAC secrets and tokens removed.

## Scope notes

WinDivert is optional and is downloaded only when Admin Mode is enabled. User Mode must remain functional without WinDivert installed.

The project does not provide or operate proxy servers. Provider availability, account access and server-side policy are outside the client security scope.
