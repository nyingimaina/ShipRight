export interface IFsEntry {
  name: string;
  path: string;
  isDirectory: boolean;
}

export interface IFsListing {
  path: string;
  parent: string | null;
  entries: IFsEntry[];
}

export interface IFsShortcut {
  label: string;
  path: string;
}

export interface IFsShortcuts {
  commonFolders: IFsShortcut[];
  drives: IFsShortcut[];
  wsl: IFsShortcut[];
}

export interface ISshConfig {
  host: string;
  user: string;
  keyPath: string;
}
