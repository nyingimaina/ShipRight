import { useRouter } from "next/router";
import SidekickMenu from "jattac.libs.web.zest-sidekick-menu";
import {
  RiStackLine,
  RiTimeLine,
  RiTerminalBoxLine,
  RiDatabase2Line,
  RiServerLine,
  RiCalendarCheckLine,
  RiGitBranchLine,
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

  const menuItems = [
    {
      id: "projects",
      label: "Projects",
      icon: <RiStackLine size={18} />,
      searchTerms: "projects",
      description: "Your apps and their environments",
      onClick: () => router.push("/projects"),
    },
    {
      id: "team",
      label: "Team",
      icon: <RiTeamLine size={18} />,
      searchTerms: "team people teammates",
      onClick: () => router.push("/team"),
    },
    {
      id: "servers",
      label: "Servers",
      icon: <RiServerLine size={18} />,
      searchTerms: "servers hosts ssh",
      description: "Hosts ShipRight can deploy to",
      onClick: () => router.push("/servers"),
    },
    {
      id: "tailnets",
      label: "Tailscale",
      icon: <RiLinksLine size={18} />,
      searchTerms: "tailscale tailnet vpn private network",
      description: "Optional private networks",
      onClick: () => router.push("/tailnets"),
    },
    {
      id: "resources",
      label: "Resources",
      icon: <RiBox3Line size={18} />,
      searchTerms: "resources shared reusable registry script",
      description: "Shared keys, registries and scripts",
      onClick: () => router.push("/resources"),
    },
    {
      id: "pipelines",
      label: "Pipelines",
      icon: <RiListCheck size={18} />,
      searchTerms: "pipelines build deploy scripts",
      description: "Reusable build and deploy flows",
      onClick: () => router.push("/pipelines"),
    },
    {
      id: "terminal",
      label: "Terminal",
      icon: <RiTerminalBoxLine size={18} />,
      searchTerms: "terminal ssh",
      onClick: () => router.push("/terminal"),
    },
    {
      id: "databases",
      label: "Databases",
      icon: <RiDatabase2Line size={18} />,
      searchTerms: "databases db sql",
      onClick: () => router.push("/databases"),
    },
    {
      id: "history",
      label: "History",
      icon: <RiTimeLine size={18} />,
      searchTerms: "history builds",
      onClick: () => router.push("/history"),
    },
    {
      id: "backups",
      label: "Backups",
      icon: <RiCalendarCheckLine size={18} />,
      searchTerms: "backups scheduler",
      description: "Scheduled database backups",
      onClick: () => router.push("/backups"),
    },
    {
      id: "utilities",
      label: "Utilities",
      icon: <RiHardDriveLine size={18} />,
      searchTerms: "utilities disk space clean prune docker free storage",
      onClick: () => router.push("/utilities"),
    },
    {
      id: "watch-branch",
      label: "Watch Branch",
      icon: <RiGitBranchLine size={18} />,
      searchTerms: "watch branch auto build ci",
      onClick: () => router.push("/watch-branch"),
    },
    {
      id: "monitoring",
      label: "Monitoring",
      icon: <RiHeartPulseLine size={18} />,
      searchTerms: "monitoring health metrics cpu ram disk",
      onClick: () => router.push("/monitoring"),
    },
    {
      id: "settings",
      label: "Settings",
      icon: <RiSettings3Line size={18} />,
      searchTerms: "settings preferences time zone companies appearance account",
      description: "Your time zone, look and companies",
      onClick: () => router.push("/settings"),
    },
  ];

  return (
    <>
      <SidekickMenu
        items={menuItems.filter((item) => {
          if (!isAdmin && (item.id === "team" || item.id === "tailnets")) return false;
          // The Utilities page manages the Windows build machine (WSL, diskpart); it has no meaning in cloud mode.
          if (mode === "cloud" && item.id === "utilities") return false;
          // Settings holds a person's sign-in preferences; desktop mode has no signed-in person.
          if (mode === "desktop" && item.id === "settings") return false;
          return true;
        })}
        searchEnabled={false}
        favouritesEnabled
        favouritesOptions={{ maxItems: 8, minToShowTab: 3, windowDays: 30 }}
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
    </>
  );
}