#include "stdafx.h"
#include "ExternalSearchSettingsDlg.h"
#include "../client/Text.h"
#include "DialogLayout.h"
#include "WinUtil.h"
#include "ImageLists.h"
#include <boost/algorithm/string/trim.hpp>

namespace
{
    class TorznabSourceDlg : public CDialogImpl<TorznabSourceDlg>
    {
    public:
        enum { IDD = IDD_EXTERNAL_SEARCH_TORZNAB };
        ExternalSearchManager::TorznabSource source;

        BEGIN_MSG_MAP(TorznabSourceDlg)
            MESSAGE_HANDLER(WM_INITDIALOG, onInitDialog)
            COMMAND_ID_HANDLER(IDOK, onOK)
            COMMAND_ID_HANDLER(IDCANCEL, onCancel)
        END_MSG_MAP()

        LRESULT onInitDialog(UINT, WPARAM, LPARAM, BOOL&)
        {
            SetWindowText(CTSTRING(EXTERNAL_SEARCH_TORZNAB_SOURCE));
            SetDlgItemText(IDC_EXT_TZ_ENABLED, CTSTRING(EXTERNAL_SEARCH_ENABLED));
            SetDlgItemText(IDC_EXT_LABEL_TZ_NAME, CTSTRING(NAME));
            SetDlgItemText(IDC_EXT_LABEL_TZ_URL, _T("URL:"));
            SetDlgItemText(IDC_EXT_LABEL_TZ_APIKEY, CTSTRING(EXTERNAL_SEARCH_API_KEY));
            SetDlgItemText(IDOK, CTSTRING(OK));
            SetDlgItemText(IDCANCEL, CTSTRING(CANCEL));
            CheckDlgButton(IDC_EXT_TZ_ENABLED, source.enabled ? BST_CHECKED : BST_UNCHECKED);
            SetDlgItemText(IDC_EXT_TZ_NAME, Text::toT(source.name).c_str());
            SetDlgItemText(IDC_EXT_TZ_URL, Text::toT(source.url).c_str());
            SetDlgItemText(IDC_EXT_TZ_APIKEY, Text::toT(source.apiKey).c_str());
            CenterWindow(GetParent());
            return TRUE;
        }

        LRESULT onOK(WORD, WORD, HWND, BOOL&)
        {
            tstring value;
            source.enabled = IsDlgButtonChecked(IDC_EXT_TZ_ENABLED) == BST_CHECKED;
            WinUtil::getWindowText(GetDlgItem(IDC_EXT_TZ_NAME), value);
            boost::trim(value);
            source.name = Text::fromT(value);
            WinUtil::getWindowText(GetDlgItem(IDC_EXT_TZ_URL), value);
            boost::trim(value);
            source.url = Text::fromT(value);
            if (source.url.empty())
            {
                MessageBox(CTSTRING(EXTERNAL_SEARCH_TORZNAB_URL_REQUIRED), CTSTRING(EXTERNAL_SEARCH_SETTINGS), MB_OK | MB_ICONWARNING);
                return 0;
            }
            WinUtil::getWindowText(GetDlgItem(IDC_EXT_TZ_APIKEY), value);
            boost::trim(value);
            source.apiKey = Text::fromT(value);
            if (source.name.empty()) source.name = "Torznab";
            EndDialog(IDOK);
            return 0;
        }

        LRESULT onCancel(WORD, WORD, HWND, BOOL&)
        {
            EndDialog(IDCANCEL);
            return 0;
        }
    };

    static int getPositiveInt(HWND hWnd, int controlId, int fallback)
    {
        BOOL ok = FALSE;
        const UINT value = ::GetDlgItemInt(hWnd, controlId, &ok, FALSE);
        return ok && value > 0 ? static_cast<int>(value) : fallback;
    }
}

LRESULT ExternalSearchSettingsDlg::onInitDialog(UINT, WPARAM, LPARAM, BOOL&)
{
    SetWindowText(CTSTRING(EXTERNAL_SEARCH_SETTINGS));
    SetDlgItemText(IDC_EXT_GROUP_SLSK, CTSTRING(EXTERNAL_SEARCH_SOULSEEK));
    SetDlgItemText(IDC_EXT_SLSK_ENABLED, CTSTRING(EXTERNAL_SEARCH_ENABLED));
    SetDlgItemText(IDC_EXT_LABEL_SLSK_URL, _T("URL:"));
    SetDlgItemText(IDC_EXT_LABEL_SLSK_APIKEY, CTSTRING(EXTERNAL_SEARCH_API_KEY));
    SetDlgItemText(IDC_EXT_LABEL_TIMEOUT, CTSTRING(EXTERNAL_SEARCH_TIMEOUT));
    SetDlgItemText(IDC_EXT_LABEL_FILE_LIMIT, CTSTRING(EXTERNAL_SEARCH_FILE_LIMIT));
    SetDlgItemText(IDC_EXT_LABEL_RESPONSE_LIMIT, CTSTRING(EXTERNAL_SEARCH_RESPONSE_LIMIT));
    SetDlgItemText(IDC_EXT_GROUP_TORZNAB, CTSTRING(EXTERNAL_SEARCH_TORZNAB));
    SetDlgItemText(IDC_EXT_TORZNAB_ADD, CTSTRING(ADD3));
    SetDlgItemText(IDC_EXT_TORZNAB_EDIT, CTSTRING(EDIT));
    SetDlgItemText(IDC_EXT_TORZNAB_REMOVE, CTSTRING(REMOVE));
    SetDlgItemText(IDC_EXT_GROUP_QBT, CTSTRING(EXTERNAL_SEARCH_QBITTORRENT));
    SetDlgItemText(IDC_EXT_QBT_ENABLED, CTSTRING(EXTERNAL_SEARCH_ENABLED));
    SetDlgItemText(IDC_EXT_LABEL_QBT_URL, _T("URL:"));
    SetDlgItemText(IDC_EXT_LABEL_QBT_APIKEY, CTSTRING(EXTERNAL_SEARCH_API_KEY));
    SetDlgItemText(IDC_EXT_LABEL_QBT_USER, CTSTRING(USER));
    SetDlgItemText(IDC_EXT_LABEL_QBT_PASSWORD, CTSTRING(PASSWORD));
    SetDlgItemText(IDC_EXT_LABEL_QBT_SAVEPATH, CTSTRING(EXTERNAL_SEARCH_SAVE_PATH));
    SetDlgItemText(IDC_EXT_LABEL_QBT_CATEGORY, CTSTRING(EXTERNAL_SEARCH_CATEGORY));
    SetDlgItemText(IDC_EXT_RELOAD, CTSTRING(EXTERNAL_SEARCH_RELOAD));
    SetDlgItemText(IDOK, CTSTRING(OK));
    SetDlgItemText(IDCANCEL, CTSTRING(CANCEL));

    ctrlTorznab.Attach(GetDlgItem(IDC_EXT_TORZNAB_LIST));
    ctrlTorznab.SetExtendedListViewStyle(WinUtil::getListViewExStyle(false));
    WinUtil::setExplorerTheme(ctrlTorznab);
    ctrlTorznab.InsertColumn(0, CTSTRING(EXTERNAL_SEARCH_ENABLED), LVCFMT_LEFT, 50, 0);
    ctrlTorznab.InsertColumn(1, CTSTRING(NAME), LVCFMT_LEFT, 65, 1);
    ctrlTorznab.InsertColumn(2, _T("URL"), LVCFMT_LEFT, 178, 2);

    loadFromManager();
    loadControls();
    CenterWindow(GetParent());
    return TRUE;
}

void ExternalSearchSettingsDlg::loadFromManager()
{
    ExternalSearchManager::getInstance()->getConfig(soulseek, qbittorrent, torznab);
}

void ExternalSearchSettingsDlg::loadControls()
{
    CheckDlgButton(IDC_EXT_SLSK_ENABLED, soulseek.enabled ? BST_CHECKED : BST_UNCHECKED);
    SetDlgItemText(IDC_EXT_SLSK_URL, Text::toT(soulseek.baseUrl).c_str());
    SetDlgItemText(IDC_EXT_SLSK_APIKEY, Text::toT(soulseek.apiKey).c_str());
    SetDlgItemInt(IDC_EXT_SLSK_TIMEOUT, soulseek.searchTimeout, FALSE);
    SetDlgItemInt(IDC_EXT_SLSK_FILE_LIMIT, soulseek.fileLimit, FALSE);
    SetDlgItemInt(IDC_EXT_SLSK_RESPONSE_LIMIT, soulseek.responseLimit, FALSE);

    CheckDlgButton(IDC_EXT_QBT_ENABLED, qbittorrent.enabled ? BST_CHECKED : BST_UNCHECKED);
    SetDlgItemText(IDC_EXT_QBT_URL, Text::toT(qbittorrent.baseUrl).c_str());
    SetDlgItemText(IDC_EXT_QBT_APIKEY, Text::toT(qbittorrent.apiKey).c_str());
    SetDlgItemText(IDC_EXT_QBT_USERNAME, Text::toT(qbittorrent.username).c_str());
    SetDlgItemText(IDC_EXT_QBT_PASSWORD, Text::toT(qbittorrent.password).c_str());
    SetDlgItemText(IDC_EXT_QBT_SAVEPATH, Text::toT(qbittorrent.savePath).c_str());
    SetDlgItemText(IDC_EXT_QBT_CATEGORY, Text::toT(qbittorrent.category).c_str());
    SetDlgItemText(IDC_EXT_CONFIG_PATH, Text::toT(ExternalSearchManager::getInstance()->getConfigPath()).c_str());
    refreshTorznabList();
}

void ExternalSearchSettingsDlg::readControls()
{
    tstring value;
    soulseek.enabled = IsDlgButtonChecked(IDC_EXT_SLSK_ENABLED) == BST_CHECKED;
    WinUtil::getWindowText(GetDlgItem(IDC_EXT_SLSK_URL), value); boost::trim(value); soulseek.baseUrl = Text::fromT(value);
    WinUtil::getWindowText(GetDlgItem(IDC_EXT_SLSK_APIKEY), value); boost::trim(value); soulseek.apiKey = Text::fromT(value);
    soulseek.searchTimeout = getPositiveInt(m_hWnd, IDC_EXT_SLSK_TIMEOUT, 15);
    soulseek.fileLimit = getPositiveInt(m_hWnd, IDC_EXT_SLSK_FILE_LIMIT, 1000);
    soulseek.responseLimit = getPositiveInt(m_hWnd, IDC_EXT_SLSK_RESPONSE_LIMIT, 100);

    qbittorrent.enabled = IsDlgButtonChecked(IDC_EXT_QBT_ENABLED) == BST_CHECKED;
    WinUtil::getWindowText(GetDlgItem(IDC_EXT_QBT_URL), value); boost::trim(value); qbittorrent.baseUrl = Text::fromT(value);
    WinUtil::getWindowText(GetDlgItem(IDC_EXT_QBT_APIKEY), value); boost::trim(value); qbittorrent.apiKey = Text::fromT(value);
    WinUtil::getWindowText(GetDlgItem(IDC_EXT_QBT_USERNAME), value); boost::trim(value); qbittorrent.username = Text::fromT(value);
    WinUtil::getWindowText(GetDlgItem(IDC_EXT_QBT_PASSWORD), value); qbittorrent.password = Text::fromT(value);
    WinUtil::getWindowText(GetDlgItem(IDC_EXT_QBT_SAVEPATH), value); boost::trim(value); qbittorrent.savePath = Text::fromT(value);
    WinUtil::getWindowText(GetDlgItem(IDC_EXT_QBT_CATEGORY), value); boost::trim(value); qbittorrent.category = Text::fromT(value);
}

void ExternalSearchSettingsDlg::refreshTorznabList()
{
    ctrlTorznab.DeleteAllItems();
    for (size_t i = 0; i < torznab.size(); ++i)
    {
        const auto& source = torznab[i];
        int item = ctrlTorznab.InsertItem(static_cast<int>(i), source.enabled ? _T("+") : _T("-"));
        ctrlTorznab.SetItemText(item, 1, Text::toT(source.name).c_str());
        ctrlTorznab.SetItemText(item, 2, Text::toT(source.url).c_str());
    }
    updateTorznabButtons();
}

int ExternalSearchSettingsDlg::selectedTorznab() const
{
    return ctrlTorznab.GetNextItem(-1, LVNI_SELECTED);
}

void ExternalSearchSettingsDlg::updateTorznabButtons()
{
    const bool selected = selectedTorznab() >= 0;
    GetDlgItem(IDC_EXT_TORZNAB_EDIT).EnableWindow(selected ? TRUE : FALSE);
    GetDlgItem(IDC_EXT_TORZNAB_REMOVE).EnableWindow(selected ? TRUE : FALSE);
}

LRESULT ExternalSearchSettingsDlg::onTorznabAdd(WORD, WORD, HWND, BOOL&)
{
    TorznabSourceDlg dlg;
    dlg.source.enabled = true;
    dlg.source.name = "Torznab";
    if (dlg.DoModal(m_hWnd) == IDOK)
    {
        torznab.push_back(dlg.source);
        refreshTorznabList();
    }
    return 0;
}

LRESULT ExternalSearchSettingsDlg::onTorznabEdit(WORD, WORD, HWND, BOOL&)
{
    const int index = selectedTorznab();
    if (index < 0 || index >= static_cast<int>(torznab.size())) return 0;
    TorznabSourceDlg dlg;
    dlg.source = torznab[index];
    if (dlg.DoModal(m_hWnd) == IDOK)
    {
        torznab[index] = dlg.source;
        refreshTorznabList();
        ctrlTorznab.SetItemState(index, LVIS_SELECTED | LVIS_FOCUSED, LVIS_SELECTED | LVIS_FOCUSED);
    }
    return 0;
}

LRESULT ExternalSearchSettingsDlg::onTorznabRemove(WORD, WORD, HWND, BOOL&)
{
    const int index = selectedTorznab();
    if (index < 0 || index >= static_cast<int>(torznab.size())) return 0;
    torznab.erase(torznab.begin() + index);
    refreshTorznabList();
    return 0;
}

LRESULT ExternalSearchSettingsDlg::onTorznabDblClick(int, LPNMHDR, BOOL&)
{
    BOOL dummy = FALSE;
    return onTorznabEdit(0, 0, nullptr, dummy);
}

LRESULT ExternalSearchSettingsDlg::onTorznabSelectionChanged(int, LPNMHDR, BOOL&)
{
    updateTorznabButtons();
    return 0;
}

LRESULT ExternalSearchSettingsDlg::onReload(WORD, WORD, HWND, BOOL&)
{
    ExternalSearchManager::getInstance()->reloadConfig();
    loadFromManager();
    loadControls();
    return 0;
}

LRESULT ExternalSearchSettingsDlg::onOK(WORD, WORD, HWND, BOOL&)
{
    readControls();
    if (soulseek.enabled && soulseek.baseUrl.empty())
    {
        MessageBox(CTSTRING(EXTERNAL_SEARCH_SOULSEEK_URL_REQUIRED), CTSTRING(EXTERNAL_SEARCH_SETTINGS), MB_OK | MB_ICONWARNING);
        return 0;
    }
    if (qbittorrent.enabled && qbittorrent.baseUrl.empty())
    {
        MessageBox(CTSTRING(EXTERNAL_SEARCH_QBITTORRENT_URL_REQUIRED), CTSTRING(EXTERNAL_SEARCH_SETTINGS), MB_OK | MB_ICONWARNING);
        return 0;
    }
    if (!ExternalSearchManager::getInstance()->saveConfig(soulseek, qbittorrent, torznab))
    {
        MessageBox(CTSTRING(EXTERNAL_SEARCH_CONFIG_SAVE_FAILED), CTSTRING(EXTERNAL_SEARCH_SETTINGS), MB_OK | MB_ICONERROR);
        return 0;
    }
    EndDialog(IDOK);
    return 0;
}

LRESULT ExternalSearchSettingsDlg::onCancel(WORD, WORD, HWND, BOOL&)
{
    EndDialog(IDCANCEL);
    return 0;
}
