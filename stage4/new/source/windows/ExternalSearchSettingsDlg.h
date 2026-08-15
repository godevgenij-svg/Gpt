#ifndef EXTERNAL_SEARCH_SETTINGS_DLG_H_
#define EXTERNAL_SEARCH_SETTINGS_DLG_H_

#include "resource.h"
#include "WinUtil.h"
#include "../client/ExternalSearchManager.h"
#include <vector>

class ExternalSearchSettingsDlg : public CDialogImpl<ExternalSearchSettingsDlg>
{
public:
    enum { IDD = IDD_EXTERNAL_SEARCH_SETTINGS };

    BEGIN_MSG_MAP(ExternalSearchSettingsDlg)
        MESSAGE_HANDLER(WM_INITDIALOG, onInitDialog)
        COMMAND_ID_HANDLER(IDOK, onOK)
        COMMAND_ID_HANDLER(IDCANCEL, onCancel)
        COMMAND_ID_HANDLER(IDC_EXT_TORZNAB_ADD, onTorznabAdd)
        COMMAND_ID_HANDLER(IDC_EXT_TORZNAB_EDIT, onTorznabEdit)
        COMMAND_ID_HANDLER(IDC_EXT_TORZNAB_REMOVE, onTorznabRemove)
        COMMAND_ID_HANDLER(IDC_EXT_RELOAD, onReload)
        NOTIFY_HANDLER(IDC_EXT_TORZNAB_LIST, NM_DBLCLK, onTorznabDblClick)
        NOTIFY_HANDLER(IDC_EXT_TORZNAB_LIST, LVN_ITEMCHANGED, onTorznabSelectionChanged)
    END_MSG_MAP()

    LRESULT onInitDialog(UINT, WPARAM, LPARAM, BOOL&);
    LRESULT onOK(WORD, WORD, HWND, BOOL&);
    LRESULT onCancel(WORD, WORD, HWND, BOOL&);
    LRESULT onTorznabAdd(WORD, WORD, HWND, BOOL&);
    LRESULT onTorznabEdit(WORD, WORD, HWND, BOOL&);
    LRESULT onTorznabRemove(WORD, WORD, HWND, BOOL&);
    LRESULT onReload(WORD, WORD, HWND, BOOL&);
    LRESULT onTorznabDblClick(int, LPNMHDR, BOOL&);
    LRESULT onTorznabSelectionChanged(int, LPNMHDR, BOOL&);

private:
    ExternalSearchManager::SoulseekConfig soulseek;
    ExternalSearchManager::AmuleConfig amule;
    ExternalSearchManager::QbittorrentConfig qbittorrent;
    std::vector<ExternalSearchManager::TorznabSource> torznab;
    CListViewCtrl ctrlTorznab;

    void loadFromManager();
    void loadControls();
    void readControls();
    void refreshTorznabList();
    void updateTorznabButtons();
    int selectedTorznab() const;
};

#endif // EXTERNAL_SEARCH_SETTINGS_DLG_H_
