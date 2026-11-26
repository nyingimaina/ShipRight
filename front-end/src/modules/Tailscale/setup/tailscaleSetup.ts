export const setupLinks = {
  admin: 'https://console.tailscale.com',
  general: 'https://console.tailscale.com/admin/settings/general',
  keys: 'https://console.tailscale.com/admin/settings/keys',
  credentials: 'https://console.tailscale.com/admin/settings/trust-credentials',
  acls: 'https://console.tailscale.com/admin/acls',
  machines: 'https://console.tailscale.com/admin/machines',
  aclDocs: 'https://tailscale.com/kb/1018/acls',
  sshDocs: 'https://tailscale.com/kb/1193/tailscale-ssh',
  authKeyDocs: 'https://tailscale.com/kb/1085/auth-keys',
} as const;

export const SETUP_PROGRESS_KEY = 'shipright.tailscale.setup.v1';

export interface SetupLink {
  label: string;
  href: string;
  /** Internal links open in the same tab; Tailscale links open in a new one. */
  internal?: boolean;
}

export interface SetupSnippet {
  label: string;
  code: string;
  language: 'json' | 'bash';
  note?: string;
}

export interface SetupAlternative {
  title: string;
  details: string[];
  snippets?: SetupSnippet[];
  links?: SetupLink[];
}

export interface SetupStep {
  id: string;
  title: string;
  summary: string;
  details: string[];
  links?: SetupLink[];
  snippets?: SetupSnippet[];
  alternatives?: SetupAlternative[];
  warning?: string;
}

export interface TroubleshootingItem {
  symptom: string;
  fix: string;
  links?: SetupLink[];
}

const TAG_OWNERS = `"tagOwners": {
  "tag:shipright": ["autogroup:admin"],
  "tag:servers": ["autogroup:admin"]
}`;

const GRANT_TAGGED_SERVERS = `{
  "src": ["tag:shipright"],
  "dst": ["tag:servers"],
  "ip": ["tcp:22"]
}`;

const GRANT_ANY_HOST = `{
  "src": ["tag:shipright"],
  "dst": ["*"],
  "ip": ["tcp:22"]
}`;

const SSH_RULE = `{
  "action": "accept",
  "src": ["tag:shipright"],
  "dst": ["tag:servers"],
  "users": ["ubuntu"]
}`;

export const setupSteps: SetupStep[] = [
  {
    id: 'tailnet',
    title: 'Find your tailnet name',
    summary: 'The name ShipRight needs, and why “-” is usually the right answer.',
    details: [
      'Sign in at the Tailscale admin console. The tailnet name is shown at the top and on Settings → General.',
      'Organizations usually see a domain such as `example.com`. A solo plan uses the email you signed up with.',
      'In ShipRight you can leave Tailnet as `-`: Tailscale resolves the default tailnet from the credential.',
      'A machine name such as `my-server.tail1234.ts.net` is not a tailnet and will be rejected.',
    ],
    links: [
      { label: 'Open Settings → General', href: setupLinks.general },
      { label: 'Open the admin console', href: setupLinks.admin },
    ],
  },
  {
    id: 'credential',
    title: 'Create a credential for ShipRight',
    summary: 'An auth key or a trust credential (OAuth client). Either is enough.',
    details: [
      'An auth key is one value and the quickest option. Open Settings → Keys → Generate auth key. Add the tag `tag:shipright`, tick Reusable if you want it to survive restarts, then copy the `tskey-auth-…` value.',
      'A trust credential is a client ID and secret. Open Settings → Trust credentials → Credential → OAuth. Select scope `Auth keys` with Write access, and select at least one tag (`tag:shipright`) — Tailscale requires a tag for this scope.',
      'OAuth clients must be owned by the tailnet, not by an individual user. If the key request returns “not found”, recreate the credential with the Auth keys scope and the tag selected.',
    ],
    links: [
      { label: 'Settings → Keys', href: setupLinks.keys },
      { label: 'Settings → Trust credentials', href: setupLinks.credentials },
      { label: 'About auth keys', href: setupLinks.authKeyDocs },
    ],
  },
  {
    id: 'tags',
    title: 'Declare the tags in your ACL',
    summary: 'Tailscale needs to know who may own tag:shipright and tag:servers.',
    details: [
      'Open Access controls, switch to the JSON editor, and add these entries to the existing `tagOwners` object. Do not replace the rest of the policy file.',
      '`tag:shipright` is the identity ShipRight’s node joins with. `tag:servers` is the identity you give the machines ShipRight should manage.',
    ],
    links: [
      { label: 'Open Access controls', href: setupLinks.acls },
      { label: 'ACL syntax reference', href: setupLinks.aclDocs },
    ],
    snippets: [
      {
        label: 'tagOwners',
        code: TAG_OWNERS,
        language: 'json',
        note: 'Merge this into the existing tagOwners object; keep your other tags.',
      },
    ],
  },
  {
    id: 'grant',
    title: 'Allow ShipRight to reach SSH',
    summary: 'A tagged node is not a member, so it needs its own grant.',
    details: [
      'Add one object to the existing `grants` array. The tight version only allows servers tagged `tag:servers` on port 22.',
      'If you would rather not tag the destination servers, use the broad version: it allows any host, still limited to SSH.',
    ],
    links: [{ label: 'Open Access controls', href: setupLinks.acls }],
    snippets: [
      {
        label: 'Tight grant (tagged servers)',
        code: GRANT_TAGGED_SERVERS,
        language: 'json',
        note: 'Only works for machines that carry tag:servers (see the next step).',
      },
    ],
    alternatives: [
      {
        title: 'Broad grant: any host, port 22 only',
        details: ['Use this if you do not want to tag every destination machine.'],
        snippets: [{ label: 'Broad grant', code: GRANT_ANY_HOST, language: 'json' }],
      },
    ],
  },
  {
    id: 'server-tags',
    title: 'Tag the servers ShipRight should manage',
    summary: 'A user-owned device does not match a tag:servers grant.',
    details: [
      'Open Machines, choose the server, then Edit ACL tags and add `tag:servers`.',
      'This is the step most people miss: native `tailscale ssh` works because you are a member, while ShipRight’s node is tagged and needs this target tag (or the broad grant).',
      'Tagging a device makes it tag-owned; human access still works through your normal member access.',
    ],
    links: [{ label: 'Open Machines', href: setupLinks.machines }],
  },
  {
    id: 'ssh',
    title: 'Choose how the servers answer SSH',
    summary: 'Regular OpenSSH (recommended) or Tailscale SSH.',
    details: [
      'ShipRight connects with SSH keys. Regular OpenSSH is the predictable path: make sure `sshd` is running, ShipRight’s public key is in `~/.ssh/authorized_keys`, and Tailscale SSH is off on that server.',
      'If the server answers with `SSH-2.0-Tailscale`, Tailscale SSH is enabled. It ignores ShipRight’s key and uses Tailscale identity/ACL instead. Either disable it, or add an SSH rule and keep the server tagged `tag:servers`.',
    ],
    links: [{ label: 'Tailscale SSH documentation', href: setupLinks.sshDocs }],
    alternatives: [
      {
        title: 'Regular OpenSSH (recommended)',
        details: ['Run this on each managed server, then test the connection.'],
        snippets: [
          { label: 'Disable Tailscale SSH', code: 'sudo tailscale set --ssh=false', language: 'bash' },
        ],
      },
      {
        title: 'Keep Tailscale SSH',
        details: [
          'Add this object to the existing `ssh` array. The server must still be tagged `tag:servers`.',
          'If ShipRight still aborts after saving, use regular OpenSSH instead.',
        ],
        snippets: [{ label: 'SSH rule', code: SSH_RULE, language: 'json' }],
      },
    ],
  },
  {
    id: 'shipright',
    title: 'Add the network in ShipRight',
    summary: 'Save the credential, then start the node.',
    details: [
      'Fill in a name, leave Tailnet as `-` unless you need a different one, paste the auth key or the trust credential, and keep Tags as `tag:shipright`.',
      'Save the network, press Start, and wait for the card to say Online. ShipRight runs tailscaled in userspace and exposes a loopback SOCKS5 proxy for that company.',
    ],
  },
  {
    id: 'assign',
    title: 'Assign a server and test',
    summary: 'A server only uses the tailnet when it is assigned to one.',
    details: [
      'Open Servers, edit the server, and choose the network in the Tailnet selector. Leave it empty to keep direct networking.',
      'The same selector is in the Guided setup wizard (route “Tailscale”).',
      'Run Test connection. The checklist should show the server reachable, the key accepted, and the server details readable.',
    ],
    links: [{ label: 'Open Servers', href: '/servers', internal: true }],
  },
  {
    id: 'verify',
    title: 'Verify and keep it healthy',
    summary: 'What “working” looks like, and what to check after a restart.',
    details: [
      'The Tailscale card says Online, Test reports the SOCKS port, and the server’s connection test passes.',
      'Backups, the Files tab, the terminal, and deployments all follow the server’s route automatically — no separate setting.',
      'If a connection that used to work starts timing out, check that the server is still tagged `tag:servers` and the node is still Online.',
    ],
  },
];

export const setupTroubleshooting: TroubleshootingItem[] = [
  {
    symptom: 'Tailscale could not find the tailnet (404)',
    fix: 'Use `-` in the Tailnet field, or the name from Settings → General. If it still fails, recreate the trust credential with the Auth keys scope and at least one tag; OAuth clients must be owned by the tailnet.',
    links: [
      { label: 'Settings → General', href: setupLinks.general },
      { label: 'Trust credentials', href: setupLinks.credentials },
    ],
  },
  {
    symptom: 'invalid key',
    fix: 'The auth key has expired or was already used. Generate a new key, or use a trust credential so ShipRight mints a short-lived key each time.',
    links: [
      { label: 'Settings → Keys', href: setupLinks.keys },
      { label: 'Trust credentials', href: setupLinks.credentials },
    ],
  },
  {
    symptom: 'SOCKS5: General failure, or a timeout when testing',
    fix: 'The tailnet is connected but the ACL blocked the destination. Tag the server `tag:servers` and add the port-22 grant, or use the broad grant. `tailscale ping` can succeed while TCP 22 is still blocked.',
    links: [
      { label: 'Open Access controls', href: setupLinks.acls },
      { label: 'Open Machines', href: setupLinks.machines },
    ],
  },
  {
    symptom: 'Server reachable, but SSH is cut off or aborts',
    fix: 'The server is almost certainly answering with Tailscale SSH (`SSH-2.0-Tailscale`). Disable it with `sudo tailscale set --ssh=false`, or add the SSH rule. This is not a ShipRight key problem.',
  },
  {
    symptom: 'requested tags … are not permitted',
    fix: 'Add the tag to `tagOwners`, and make sure the auth key or trust credential was created with that tag.',
    links: [{ label: 'Open Access controls', href: setupLinks.acls }],
  },
];
