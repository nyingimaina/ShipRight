import { useState } from 'react';
import { IProject } from '@/shared/types/IProject';
import ProjectSetupWizard from '../ProjectSetupWizard';
import QuickSetup from './QuickSetup';

interface Props {
  existing?: IProject;
  onSaved: (project: IProject) => void;
  onCancel: () => void;
}

/**
 * New projects start with the guided flow; anyone who wants every field (or is editing a project)
 * gets the original wizard, unchanged.
 */
export default function ProjectPane({ existing, onSaved, onCancel }: Props) {
  const [manual, setManual] = useState(false);

  if (existing || manual) {
    return <ProjectSetupWizard existing={existing} onSaved={onSaved} onCancel={onCancel} />;
  }
  return <QuickSetup onSaved={onSaved} onCancel={onCancel} onManual={() => setManual(true)} />;
}
