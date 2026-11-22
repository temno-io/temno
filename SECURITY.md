# Security

## Reporting a vulnerability

Email **security@temno.io**. Please do not open a public issue for a security problem.

Tell us what you found and how to reproduce it: the Temno version (**About** shows it), your Windows
version, and the steps or a proof of concept. If the problem is in the hosted side (sign-in, chat,
generation, credits or billing at temno.io), report it to the same address.

We reply from a person, keep you told as we work on a fix, and credit you in the release notes if
you would like that.

## What is in scope

- The desktop app in this repository, including its local MCP server (loopback only, token
  protected) and the scripts that build and package it.
- Temno's hosted services. Their source is not in this repository, but reports about them are
  welcome at the address above.

Please test only against your own installation and your own account, and do not access or change
anyone else's data.
