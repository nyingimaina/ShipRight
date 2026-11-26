import ZestTabs, { type ZestTabItem, type ZestTabsProps } from 'jattac.libs.web.zest-tabs';
import styles from './AppTabs.module.css';

type Props<T extends string | number> = Omit<ZestTabsProps<T>, 'classNames' | 'unstyled'> & {
  className?: string;
};

/**
 * ShipRight's tab bar. The library's stock look is green/grey and does not follow our navy-and-gold tokens, so the
 * cosmetics are switched off and supplied here once — every screen gets the same segmented control.
 */
export default function AppTabs<T extends string | number>({ className, ...props }: Props<T>) {
  return (
    <ZestTabs
      {...props}
      unstyled
      className={[styles.wrapper, className].filter(Boolean).join(' ')}
      classNames={{
        container: styles.track,
        tab: styles.tab,
        activeTab: styles.active,
        disabledTab: styles.disabled,
        activePill: styles.pill,
        scrollArrowLeft: styles.arrowLeft,
        scrollArrowRight: styles.arrowRight,
      }}
    />
  );
}

export type { ZestTabItem };
