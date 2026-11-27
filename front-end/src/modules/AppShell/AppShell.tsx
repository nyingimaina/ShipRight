import { useRouter } from "next/router";
import SidekickMenu, { type ISidekickMenuItem } from "jattac.libs.web.zest-sidekick-menu";
import {
  RiStackLine,
  RiTimeLine,
  RiTerminalBoxLine,
  RiDatabase2Line,
  RiServerLine,
  RiCalendarCheckLine,
  RiGitBranchLine,
  RiPlayCircleLine,
  RiHeartPulseLine,
  RiBox3Line,
  RiListCheck,
  RiTeamLine,
  RiHardDriveLine,
  RiLinksLine,
  RiLogoutCircleLine,
  RiSettings3Line,
} from "react-icons/ri";
import ThemeToggle from "@/shared/theme/ThemeToggle";
import { useTheme } from "@/shared/theme/ThemeContext";
import { authApi, useAuthStore } from "@/shared/auth";
import styles from "./Styles/AppShell.module.css";
import { useServerVersion, useServerMode } from "@/shared/serverInfo";
import { useInRootShell } from "@/shared/layout/ShellContext";
import CompanySwitcher from "./CompanySwitcher";
import InstallBanner from "@/shared/pwa/InstallBanner";

interface Props {
  children: React.ReactNode;
}

export default function AppShell({ children }: Props) {
  const router = useRouter();
  const clearAuth = useAuthStore((s) => s.clearAuth);
  const role = useAuthStore((s) => s.user?.role);
  const isAdmin = role === 'Admin';
  const version = useServerVersion();
  const { theme } = useTheme();
  const inRootShell = useInRootShell();
  const mode = useServerMode();

  // Pages still wrap themselves in AppShell; under the root layout that would draw a second sidebar.
  if (inRootShell) return <>{children}</>;

  const handleLogout = async () => {
    // Desktop mode has no sign-in endpoints; the call fails harmlessly and we clear anyway.
    try {
      await authApi.logout();
    } catch {
      // The cookie may already be gone; sign the person out locally regardless.
    }
    clearAuth();
    router.push("/login");
  };

  // The menu is a hierarchy, not a list: the two things a person works with first, then the tools grouped by what
  // they do. Groups collapse, so the pane never becomes a wall of unrelated destinations.
  const go = (path: string) => () => router.push(path);

  const menuItems: ISidekickMenuItem[] = [
    {
      id: "servers",
      label: "Servers",
      icon: <RiServerLine size={18} />,
      searchTerms: "servers hosts ssh machines",
      description: "The machines ShipRight owns",
      onClick: go("/servers"),
    },
    {
      id: "projects",
      label: "Projects",
      icon: <RiStackLine size={18} />,
      searchTerms: "projects apps environments",
      description: "What runs on your servers",
      onClick: go("/projects"),
    },
    {
      id: "resources",
      label: "Resources",
      icon: <RiBox3Line size={18} />,
      searchTerms: "resources shared registry script credential keys",
      description: "Shared keys, registries and scripts",
      onClick: go("/resources"),
    },
    {
      id: "ship",
      label: "Ship",
      icon: <RiListCheck size={18} />,
      searchTerms: "ship build deploy pipelines backups history watch",
      description: "Build, back up and follow deploys",
      children: [
        { id: "pipelines", label: "Pipelines", icon: <RiListCheck size={17} />, searchTerms: "pipelines build deploy scripts", onClick: go("/pipelines") },
        { id: "backups", label: "Backups", icon: <RiCalendarCheckLine size={17} />, searchTerms: "backups schedules databases", onClick: go("/backups") },
        { id: "watch-branch", label: "Watch Branch", icon: <RiGitBranchLine size={17} />, searchTerms: "watch branch auto build ci", onClick: go("/watch-branch") },
        { id: "bootstrap", label: "Bootstrap", icon: <RiPlayCircleLine size={17} />, searchTerms: "bootstrap self deploy staging first deployment from scratch", onClick: go("/bootstrap") },
        { id: "history", label: "History", icon: <RiTimeLine size={17} />, searchTerms: "history builds deploys", onClick: go("/history") },
      ],
    },
    {
      id: "operate",
      label: "Operate",
      icon: <RiHeartPulseLine size={18} />,
      searchTerms: "operate monitoring terminal databases health",
      description: "Look inside and fix what is running",
      children: [
        { id: "monitoring", label: "Monitoring", icon: <RiHeartPulseLine size={17} />, searchTerms: "monitoring health cpu ram disk", onClick: go("/monitoring") },
        { id: "terminal", label: "Terminal", icon: <RiTerminalBoxLine size={17} />, searchTerms: "terminal ssh shell", onClick: go("/terminal") },
        { id: "databases", label: "Databases", icon: <RiDatabase2Line size={17} />, searchTerms: "databases db sql query", onClick: go("/databases") },
      ],
    },
    {
      id: "workspace",
      label: "Workspace",
      icon: <RiBox3Line size={18} />,
      searchTerms: "workspace tailscale team people",
      description: "Private networks and the people using them",
      children: [
        { id: "tailnets", label: "Tailscale", icon: <RiLinksLine size={17} />, searchTerms: "tailscale tailnet vpn private network", onClick: go("/tailnets") },
        { id: "team", label: "Team", icon: <RiTeamLine size={17} />, searchTerms: "team people teammates roles", onClick: go("/team") },
      ],
    },
    {
      id: "settings",
      label: "Settings",
      icon: <RiSettings3Line size={18} />,
      searchTerms: "settings preferences time zone companies appearance account install",
      description: "Your time zone, look, companies and app",
      onClick: go("/settings"),
    },
    {
      id: "utilities",
      label: "Utilities",
      icon: <RiHardDriveLine size={18} />,
      searchTerms: "utilities disk space clean prune docker free storage",
      onClick: go("/utilities"),
    },
  ];

  const allowed = (id: string) => {
    if (!isAdmin && (id === "team" || id === "tailnets")) return false;
    // The Utilities page manages the Windows build machine (WSL, diskpart); it has no meaning in cloud mode.
    if (mode === "cloud" && id === "utilities") return false;
    // Bootstrap writes a server and a project over SSH from this machine; it only exists in desktop mode.
    if (mode === "cloud" && id === "bootstrap") return false;
    // Settings holds a person's sign-in preferences; desktop mode has no signed-in person.
    if (mode === "desktop" && id === "settings") return false;
    return true;
  };

  const visibleItems = menuItems.flatMap((item): ISidekickMenuItem[] => {
    if (!allowed(item.id)) return [];
    if (item.children) {
      const children = item.children.filter((child) => allowed(child.id));
      return children.length > 0 ? [{ ...item, children }] : [];
    }
    return [item];
  });

  return (
    <>
      <SidekickMenu
        items={visibleItems}
        navigationStyle="accordion"
        searchEnabled={false}
        favouritesEnabled={false}
        side="left"
        swipeEnabled
        railCollapsible
        onboardingHint="auto"
        menuLabel="Open ShipRight menu"
        zest={{
          theme,
          visualOptions: { size: "md", radius: "lg", surface: "translucent" },
          motionOptions: { intensity: "playful", reducedMotion: "auto" },
        }}
        headerContent={
          <div className={styles.header}>
            <div className={styles.headerTitle}>ShipRight{version && <span className={styles.headerVersion}>v{version}</span>}</div>
            <div className={styles.headerSub}>Build. Ship. Done.</div>
            <CompanySwitcher />
          </div>
        }
        footerContent={
          <div className={styles.footer}>
            <ThemeToggle />
            <button
              className={styles.logoutBtn}
              onClick={handleLogout}
              title="Sign out"
            >
              <RiLogoutCircleLine size={16} />
            </button>
          </div>
        }
      />
      <main className={styles.content}>
        <div className={styles.page}>{children}</div>
      </main>
      <InstallBanner />
    </>
  );
}