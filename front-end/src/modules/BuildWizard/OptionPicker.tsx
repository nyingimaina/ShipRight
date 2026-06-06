import { motion } from 'framer-motion';
import ZestButton from 'jattac.libs.web.zest-button';
import styles from './Styles/BuildWizard.module.css';

export interface PickerOption {
  value: string;
  label: string;
  desc?: string;
  danger?: boolean;
}

interface Props {
  options: PickerOption[];
  value: string | null;
  onChange: (v: string) => void;
  onConfirm: () => void;
  confirmDisabled?: boolean;
}

export default function OptionPicker({ options, value, onChange, onConfirm, confirmDisabled }: Props) {
  return (
    <>
      <div className={styles.optionList}>
        {options.map((opt, i) => {
          const selected = value === opt.value;
          const isDanger = opt.danger;
          const cardCls = [
            styles.optionCard,
            selected ? (isDanger ? styles.optionCardDangerSelected : styles.optionCardSelected) : '',
          ].join(' ');
          const radioCls = [
            styles.optionRadio,
            selected ? (isDanger ? styles.optionRadioDangerSelected : styles.optionRadioSelected) : '',
          ].join(' ');

          return (
            <motion.div
              key={opt.value}
              className={cardCls}
              onClick={() => onChange(opt.value)}
              initial={{ opacity: 0, y: 8 }}
              animate={{ opacity: 1, y: 0 }}
              transition={{ duration: 0.18, delay: i * 0.055, ease: 'easeOut' }}
              whileTap={{ scale: 0.98 }}
            >
              <div className={radioCls}>
                {selected && <div className={styles.optionRadioDot} />}
              </div>
              <div className={styles.optionText}>
                <div className={styles.optionLabel}>{opt.label}</div>
                {opt.desc && <div className={styles.optionDesc}>{opt.desc}</div>}
              </div>
            </motion.div>
          );
        })}
      </div>

      <div className={styles.optionConfirm}>
        <ZestButton
          onClick={onConfirm}
          disabled={confirmDisabled || value === null}
          zest={{ visualOptions: { variant: 'standard' }, semanticType: 'submit' }}
        >
          Confirm
        </ZestButton>
      </div>
    </>
  );
}
