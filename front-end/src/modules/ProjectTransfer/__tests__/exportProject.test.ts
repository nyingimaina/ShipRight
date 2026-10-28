import { exportProject } from '../exportProject';

jest.mock('@/shared/ApiService', () => ({ sseUrl: (p: string) => p }));

describe('exportProject', () => {
  const realFetch = global.fetch;
  afterEach(() => { global.fetch = realFetch; jest.restoreAllMocks(); });

  beforeEach(() => {
    (URL as any).createObjectURL = jest.fn(() => 'blob:x');
    (URL as any).revokeObjectURL = jest.fn();
  });

  it('posts to the export endpoint and saves the zip under the name the server gave', async () => {
    global.fetch = jest.fn().mockResolvedValue({
      ok: true, status: 200,
      headers: { get: () => 'attachment; filename=app-shipright-export.zip' },
      blob: async () => new Blob(['zip']),
    }) as any;
    const click = jest.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});

    await exportProject('app');

    expect(global.fetch).toHaveBeenCalledWith('/api/projects/app/export', expect.objectContaining({ method: 'POST', credentials: 'include' }));
    expect(click).toHaveBeenCalledTimes(1);
    expect((URL as any).revokeObjectURL).toHaveBeenCalled();
  });

  it('falls back to a sensible file name when the server sends none', async () => {
    global.fetch = jest.fn().mockResolvedValue({
      ok: true, status: 200, headers: { get: () => null }, blob: async () => new Blob(['zip']),
    }) as any;
    let name = '';
    jest.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) { name = this.download; });

    await exportProject('app');
    expect(name).toBe('app-shipright-export.zip');
  });

  it('throws the server message when the export fails', async () => {
    global.fetch = jest.fn().mockResolvedValue({
      ok: false, status: 404, json: async () => ({ message: "Project 'x' not found." }),
    }) as any;
    await expect(exportProject('x')).rejects.toThrow("Project 'x' not found.");
  });
});
