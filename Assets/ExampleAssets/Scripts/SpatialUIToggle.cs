using UnityEngine;
using UnityEngine.Events;
using TMPro;

namespace PolySpatial.Template
{
    public class SpatialUIToggle : SpatialUI
    {
        [SerializeField]
        public UnityEvent<bool> m_ToggleChanged;

        [SerializeField]
        MeshRenderer m_ToggleBackground;

        [SerializeField]
        TextMeshPro m_Text;

        [SerializeField]
        public string textOn;

        private string m_DefaultText;
        public bool m_Active;

        [SerializeField]
        bool textOn_Useful = false;

        public bool enableInteraction = true;

        [SerializeField]
        Material m_SelectedMaterial;

        [SerializeField]
        Material m_UnselectedMaterial;

        private void Start()
        {
            // Find TextMeshPro if not assigned
            if (m_Text == null)
            {
                m_Text = GetComponentInChildren<TextMeshPro>();
            }
            
            if (m_Text != null)
            {
                m_DefaultText = m_Text.text;
            }
        }

        public override void PressStart()
        {
            if (!enableInteraction) return;
            base.PressStart();
            m_PressStart.Invoke();
        }

        public override void PressEnd()
        {
            if (!enableInteraction) return;
            m_PressEnd.Invoke();
            base.PressEnd();
            m_Active = !m_Active;
            m_ToggleChanged.Invoke(m_Active);
            ChangeToggleAppearance();
        }

        public void PassiveToggleWithoutInvoke()
        {
            m_Active = !m_Active;
            ChangeToggleAppearance();
        }

        public void PassiveToggleWithoutInvokeOn()
        {
            m_Active = true;
            ChangeToggleAppearance();
        }

        public void PassiveToggleWithoutInvokeOff()
        {
            m_Active = false;
            ChangeToggleAppearance();
        }        

        private void ChangeToggleAppearance()
        {
            if (m_SelectedMaterial != null && m_UnselectedMaterial != null)
            {
                m_ToggleBackground.material = m_Active ? m_SelectedMaterial : m_UnselectedMaterial;
            }
            else
            {
                m_ToggleBackground.material.color = m_Active ? m_SelectedColor : m_UnselectedColor;
            }
            
            // Update TextMeshPro text based on toggle state
            if (m_Text != null && textOn_Useful)
            {
                m_Text.text = m_Active ? textOn : m_DefaultText;
            }        
        }
    }
}
